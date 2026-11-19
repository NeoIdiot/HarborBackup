using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace HarborBackup.Windows.Integration;

public static class DeviceIdentity
{
    public static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Device(string machineGuid, string sid, string? salt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineGuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(sid);
        return Digest($"HarborBackup/v1|{machineGuid}|{sid}|{salt}");
    }
    public static string CurrentDevice()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        using var identity = WindowsIdentity.GetCurrent();
        return Device(key?.GetValue("MachineGuid") as string ?? throw new InvalidOperationException("Windows installation identity unavailable."),
            identity.User?.Value ?? throw new InvalidOperationException("Windows user identity unavailable."),
            Environment.GetEnvironmentVariable("HARBORBACKUP_DEVICE_IDENTITY"));
    }
    public static string Task(string source, string destination) => Digest(source.ToLowerInvariant() + '|' + destination.ToLowerInvariant());
    public static string Fingerprint(IEnumerable<string> directories, IEnumerable<string> files, string target) =>
        Digest(string.Join('|', directories) + "||" + string.Join('|', files) + "||" + target.ToLowerInvariant());
}
