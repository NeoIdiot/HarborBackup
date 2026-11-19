namespace HarborBackup.Windows.Integration;

/// <summary>
/// Matches packaging/windows/HarborBackup.iss. The short startup gate closes the race between
/// starting a process and starting maintenance. Marker handles live for the entire process.
/// </summary>
public sealed class InstallationGuard : IDisposable
{
    private readonly Mutex running;
    public static string Namespace => @"Global\HarborBackup." + Environment.UserName.ToUpperInvariant();

    public InstallationGuard(string? name = null)
    {
        name ??= Namespace;
        using var gate = new Mutex(false, name + ".Startup");
        var held = false;
        try
        {
            try { held = gate.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("安装状态正忙，请稍后再试。");
            if (Mutex.TryOpenExisting(name + ".Maintenance", out var maintenance))
            {
                maintenance.Dispose();
                throw new IOException("HarborBackup 正在安装或卸载，请完成后再启动。");
            }
            running = new Mutex(false, name + ".Running");
        }
        finally { if (held) gate.ReleaseMutex(); }
    }

    public void Dispose() => running.Dispose();
}
