using HarborBackup.Desktop.Presentation;
using HarborBackup.Windows.Integration;
using HarborBackup.Desktop.Lifecycle;
using System.Diagnostics;
using HarborBackup.Desktop.Localization;

namespace HarborBackup.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var openIssues = args is [WindowsNotification.OpenIssuesArgument];
        var background = args.Contains("--background");
        try
        {
            using var installation = new InstallationGuard();
            var channelName = "HarborBackup.Show-" + DeviceIdentity.CurrentDevice() + "-" + Process.GetCurrentProcess().SessionId;
            if (args is ["--shutdown"])
            {
                Environment.ExitCode = InstanceActivation.ShutdownAsync(channelName).GetAwaiter().GetResult() ? 0 : 2;
                return;
            }
            var paths = ParsePaths(openIssues ? [] : args);
            using var instance = new Mutex(false, @"Local\HarborBackup.Desktop-" + DeviceIdentity.CurrentDevice());
            bool acquired;
            try { acquired = instance.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                Environment.ExitCode = background ||
                    (openIssues ? InstanceActivation.ShowIssuesAsync(channelName) : InstanceActivation.ShowAsync(channelName)).GetAwaiter().GetResult() ? 0 : 2;
                return;
            }
            try
            {
                var preferenceIndex = Array.IndexOf(args, "--preferences");
                using var context = new TrayContext(paths, startHidden: background || openIssues,
                    preferencesPath: preferenceIndex < 0 ? null : args[preferenceIndex + 1]);
                using var activation = new InstanceActivation(channelName, context.RequestActivation, context.RequestShutdown, context.RequestIssues);
                if (openIssues) context.RequestIssues();
                Application.Run(context);
            }
            finally { instance.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            var title = UiText.Text("HarborBackup 无法启动");
            var message = UiText.Text(CloudAccountPresentation.Error(ex));
            if (!Task.Run(() => WindowsNotification.TryShow(title, message)).GetAwaiter().GetResult() && !background)
                MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static RuntimePaths ParsePaths(string[] args)
    {
        var options = new Dictionary<string, string>();
        var background = false;
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--background")
            {
                if (background) throw new ArgumentException("Duplicate --background.");
                background = true;
                continue;
            }
            if (key is not ("--config" or "--system-config" or "--preferences") || ++i == args.Length ||
                args[i].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(key, args[i]))
                throw new ArgumentException("托盘参数：--config <文件> --system-config <文件>");
        }
        return ApplicationPaths.Resolve(options.GetValueOrDefault("--config"), options.GetValueOrDefault("--system-config"));
    }
}
