using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarborBackup.Core.Application;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Cli;

internal static class Program
{
    // Intentionally synchronous host: Mutex ownership stays on this thread while async I/O runs elsewhere.
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length > 0 && args[0] == "installation-prepare")
        {
            try
            {
                if (args.Length != 3 || args[1] != "--lease") throw new ArgumentException("An active installer lease is required.");
                Console.WriteLine(JsonSerializer.Serialize(FreshDataInitializer.PrepareInstallation(ApplicationPaths.Resolve(), args[2]), AtomicJson.Options));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Length > 0 && args[0] == "initialize")
        {
            try
            {
                if (args.Length is not (2 or 4) || args[1] != "--fresh" || args.Length == 4 && args[2] != "--config")
                    throw new ArgumentException("Use initialize --fresh [--config <absolute data/config/projects.json>]. Exit the desktop app first.");
                var paths = ApplicationPaths.Resolve(args.Length == 4 ? args[3] : null, null);
                Console.WriteLine(JsonSerializer.Serialize(FreshDataInitializer.Recreate(paths), AtomicJson.Options));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        InstallationGuard installation;
        try { installation = new InstallationGuard(); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }
        using var installationLifetime = installation;
        if (args.Length == 0 || args is ["--help"] or ["help"])
        {
            Console.WriteLine("HarborBackup — OneDrive account backup, protected replacement and recovery\n" +
                "cloud-connect [--client-id <id>] | cloud-bind [--name HarborBackup] | cloud-upgrade | cloud-folders | cloud-status | cloud-disconnect\ncloud-inspect|cloud-resolve --destination <project> --path <file> [--prefer local|<revision> --expected <confirmation>]\npreview|backup|pull|recover|verify|status|devices|pause|resume\n" +
                "        [--config <absolute projects.json>] [--system-config <absolute backup-settings.json>]\n" +
                "        [--direction Backup|Pull]\n" +
                "        [--project-id <id> | --destination <path>] [--full-check] [--json] [--automatic]\n" +
                "initialize --fresh [--config <absolute data/config/projects.json>] (exit all apps first; retains old data)\n" +
                "        preview --prepare-upload | backup|pull --selection <preview.json>\n" +
                "restore-list [--project-id <id>|--destination <path>] [--scope current|archive|all] [--path <relative prefix>] [--json]\n" +
                "restore|restore-test --output <independent directory> [same selectors] [--json]\n" +
                "        restore-test [--max-bytes <bytes>] [--groups <1-256>] (rotates groups; downloads into a fresh run directory)\n" +
                "Pull requires --destination. Verify is read-only; backup --full-check also writes.\n" +
                "Automatic backup budgets use schedule.automaticMaxTransferMiB/automaticMaxDurationMinutes (0 = unlimited). Budget cutoff returns 2 after active files finish.\n" +
                "Exit codes: 0 complete, 1 failure, 2 attention/busy, 130 cancelled.\n" +
                "Cloud comparison reads metadata only. Full checks hash local files. Choose a management folder and a separate project destination.");
            return 0;
        }
        try
        {
            if (args[0].StartsWith("cloud-", StringComparison.Ordinal)) return CloudCommands.Run(args);
            if (args[0] is "restore-list" or "restore" or "restore-test") return RestoreCommands.Run(args);
            if (args[0] is not ("preview" or "backup" or "pull" or "recover" or "verify" or "status" or "devices" or "pause" or "resume"))
                throw new ArgumentException("Unknown command. Use --help.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i++)
            {
                var option = args[i];
                if (option is "--json" or "--full-check" or "--automatic" or "--prepare-upload")
                {
                    if (!flags.Add(option)) throw new ArgumentException($"Duplicate option: {option}");
                }
                else if (option is "--config" or "--system-config" or "--direction" or "--destination" or "--project-id" or "--selection")
                {
                    if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(option, args[i]))
                        throw new ArgumentException($"Missing or duplicate value: {option}");
                }
                else throw new ArgumentException($"Unknown option: {option}");
            }
            var directionText = options.GetValueOrDefault("--direction", "Backup");
            if (!Enum.TryParse<SyncDirection>(directionText, true, out var direction) || !Enum.IsDefined(direction) ||
                !new[] { "Backup", "Pull" }.Contains(directionText, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Direction must be Backup or Pull.");
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            if (options.ContainsKey("--direction") && args[0] is not ("preview" or "verify"))
                throw new ArgumentException("--direction is only valid for preview/verify.");
            var runtime = new BackupRuntime(ApplicationPaths.Resolve(options.GetValueOrDefault("--config"),
                options.GetValueOrDefault("--system-config")));
            if (options.ContainsKey("--destination") && options.ContainsKey("--project-id")) throw new ArgumentException("Choose one project selector.");
            var destination = options.GetValueOrDefault("--project-id") ?? options.GetValueOrDefault("--destination");
            if (options.ContainsKey("--project-id") && !FileVersion.IsHash(destination ?? "")) throw new ArgumentException("Invalid project ID.");
            if (flags.Contains("--automatic") && (args[0] != "backup" || options.ContainsKey("--selection")))
                throw new ArgumentException("--automatic requires backup without a selection.");
            if (flags.Contains("--prepare-upload") && (args[0] != "preview" || direction != SyncDirection.Backup || destination is null))
                throw new ArgumentException("--prepare-upload requires preview --destination with Backup direction.");
            if (options.ContainsKey("--selection") && (args[0] is not ("backup" or "pull") || destination is null))
                throw new ArgumentException("--selection requires backup/pull --destination.");
            if (args[0] == "devices")
            {
                using var runLock = new RunLock();
                var config = runtime.Load();
                if (flags.Contains("--full-check")) throw new ArgumentException("devices only reads metadata.");
                Console.WriteLine(JsonSerializer.Serialize(new CloudBackupRuntime(config).RevisionsAsync(destination, cancellation.Token).GetAwaiter().GetResult(), AtomicJson.Options));
                return 0;
            }
            if (args[0] is "status" or "pause" or "resume")
            {
                if (destination is not null || flags.Contains("--full-check")) throw new ArgumentException("Unsupported option for control/status command.");
                using var runLock = new RunLock();
                var config = runtime.Load();
                var control = new DeviceControlStore(config.Preview);
                if (args[0] != "status") control.SetPaused(args[0] == "pause");
                Console.WriteLine(JsonSerializer.Serialize(new { paused = control.ReadPaused(), cloudUploadStatus = "AccountTransport", status = RunStatusStore.Read(config.Preview) }, AtomicJson.Options));
                return 0;
            }
            if (args[0] is "backup" or "pull" or "recover")
            {
                PullSelection? selection = null;
                if (options.TryGetValue("--selection", out var selectionPath))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(PathRules.Absolute(selectionPath)));
                    var property = args[0] == "pull" ? "pullSelection" : "uploadSelection";
                    var element = document.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase)).Value;
                    selection = element.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<PullSelection>(element, AtomicJson.Options) : null;
                    if (selection is null) throw new FormatException("The preview has no matching operation selection.");
                }
                var result = runtime.Execute(args[0] == "pull" ? SyncDirection.Pull : SyncDirection.Backup,
                    destination, flags.Contains("--full-check"), args[0] == "recover", token: cancellation.Token,
                    confirmedPull: args[0] == "pull" ? selection : null, automatic: flags.Contains("--automatic"),
                    confirmedUpload: args[0] == "backup" ? selection : null);
                if (flags.Contains("--json")) Console.WriteLine(JsonSerializer.Serialize(result, AtomicJson.Options));
                else
                {
                    Console.WriteLine($"{result.Mode} | 结果={result.ExitCode} | {result.CloudUploadStatus}");
                    if (result.BudgetStopReason is { } budgetReason) Console.WriteLine($"自动预算停止：{budgetReason}；未开始的文件留待下轮继续。");
                    foreach (var project in result.Projects)
                    {
                        Console.WriteLine($"项目：{project.Destination} | 结果={project.ExitCode}");
                        foreach (var file in project.Files) Console.WriteLine($"  {file.Action,-20} {file.Path} | 修改={file.Changed} | {file.FailureKind} | {file.Error}");
                        foreach (var issue in project.Issues) Console.Error.WriteLine($"  {issue.Code}: {issue.Message}");
                    }
                }
                return result.ExitCode;
            }
            var report = runtime.Preview(direction, destination, flags.Contains("--full-check") || args[0] == "verify", cancellation.Token,
                prepareUpload: flags.Contains("--prepare-upload"), verifyOnly: args[0] == "verify");
            if (flags.Contains("--json"))
            {
                Console.WriteLine(JsonSerializer.Serialize(report, AtomicJson.Options));
            }
            else
            {
                Console.WriteLine("只读预览：不改写业务文件。云端只读取元数据；完整核查只哈希本地文件。缓存可更新。");
                var verification = report.Verification;
                Console.WriteLine($"核验分类：一致={verification.Match} 不同={verification.Different} 本地缺失={verification.MissingLocal} 云端缺失={verification.MissingCloud} 无法确认={verification.Unverifiable} 类型冲突={verification.TypeConflicts}");
                Console.WriteLine($"运行计划：传输={report.Summary.Transfers} 保护旧内容={report.Summary.Protected} 待处理={report.Summary.NeedsAttention} 字节={report.Summary.TransferBytes}");
                foreach (var project in report.Projects)
                {
                    Console.WriteLine($"项目：{project.Destination}");
                    foreach (var plan in project.Plans)
                    {
                        Console.WriteLine($"  {plan.Decision.Action,-20} {plan.Path} | {plan.Decision.Reason} | 保护={plan.Decision.Protection}/{plan.Decision.ProtectSide}");
                        if (plan.SourceError is not null) Console.Error.WriteLine($"    源：{plan.SourceError}");
                        if (plan.TargetError is not null) Console.Error.WriteLine($"    目标：{plan.TargetError}");
                    }
                    foreach (var issue in project.Issues) Console.Error.WriteLine($"  {issue.Code}: {issue.Message}");
                    Console.WriteLine($"  哈希文件={project.Metrics.HashedFiles}, 字节={project.Metrics.HashBytes}, 缓存={project.Metrics.CachedFiles}, 结果={project.ExitCode}");
                }
            }
            return report.ExitCode;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled at a safe boundary; completed file transactions remain committed."); return 130; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or ArgumentException or FormatException or
            InvalidOperationException or JsonException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            Console.Error.WriteLine(CloudNetworkAvailability.FriendlyMessage(ex) ?? ex.Message);
            return ex.Message.StartsWith("Busy:", StringComparison.Ordinal) ? 2 : 1;
        }
    }
}
