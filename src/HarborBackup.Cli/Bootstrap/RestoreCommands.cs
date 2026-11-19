using System.Globalization;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Cli;

internal static class RestoreCommands
{
    internal static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(); var json = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--json") { if (json) throw new ArgumentException("Duplicate --json."); json = true; continue; }
            var name = args[i];
            if (name is not ("--config" or "--system-config" or "--project-id" or "--destination" or "--scope" or "--path" or "--output" or "--max-bytes" or "--groups") ||
                ++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(name, args[i]))
                throw new ArgumentException("Unknown, missing or duplicate restore option: " + name);
        }
        if (options.ContainsKey("--project-id") && options.ContainsKey("--destination")) throw new ArgumentException("Choose one project selector.");
        var destination = options.GetValueOrDefault("--project-id") ?? options.GetValueOrDefault("--destination");
        if (options.ContainsKey("--project-id") && !FileVersion.IsHash(destination ?? "")) throw new ArgumentException("Invalid project ID.");
        var scopeText = options.GetValueOrDefault("--scope", "current").ToLowerInvariant();
        RestoreScope? scope = scopeText switch { "current" => RestoreScope.Current, "archive" => RestoreScope.Archive, "all" => null, _ => throw new ArgumentException("Scope must be current, archive or all.") };
        var list = args[0] == "restore-list"; var test = args[0] == "restore-test";
        if (list && options.ContainsKey("--output") || !test && (options.ContainsKey("--max-bytes") || options.ContainsKey("--groups")))
            throw new ArgumentException("Unsupported restore option for this command.");
        if (!list && !options.ContainsKey("--output")) throw new ArgumentException("Restore requires --output <independent directory>.");
        if (!long.TryParse(options.GetValueOrDefault("--max-bytes", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out var maxBytes) ||
            !int.TryParse(options.GetValueOrDefault("--groups", "1"), NumberStyles.None, CultureInfo.InvariantCulture, out var groups) || groups is < 1 or > 256)
            throw new ArgumentException("Invalid restore-test budget or group count.");
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        var runtime = new BackupRuntime(ApplicationPaths.Resolve(options.GetValueOrDefault("--config"), options.GetValueOrDefault("--system-config")));
        var catalog = runtime.ListRestore(destination, scope, options.GetValueOrDefault("--path"), cancellation.Token);
        if (list)
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(catalog, AtomicJson.Options));
            else foreach (var candidate in catalog.Candidates) Console.WriteLine($"{candidate.Scope} | {candidate.Destination}/{candidate.Path} | {candidate.Length} bytes | SHA-256={(candidate.Sha256 is null ? "unavailable" : candidate.Sha256)}");
            return catalog.Diagnostics.Count > 0 ? 2 : 0;
        }
        var report = runtime.Restore(catalog.Candidates, options["--output"], test, maxBytes, groups, cancellation.Token);
        if (json) Console.WriteLine(JsonSerializer.Serialize(report, AtomicJson.Options));
        else
        {
            Console.WriteLine($"{report.Mode} | verified={report.VerifiedFiles} | bytes={report.VerifiedBytes} | output={report.OutputRoot}");
            foreach (var file in report.Files) Console.WriteLine($"{file.Status} | {file.Scope}/{file.Path} | {file.Error}");
            foreach (var diagnostic in report.Diagnostics) Console.Error.WriteLine(diagnostic);
        }
        return report.ExitCode;
    }
}
