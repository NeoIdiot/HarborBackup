using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HarborBackup.Windows.FileSystem;

public sealed class UnsafePathException(string message) : IOException(message);

public static class SafePath
{
    public static void Validate(string path, string? root = null)
    {
        var full = PathRules.Normalize(path);
        if (root is not null && !PathRules.Within(full, root)) throw new UnsafePathException("Path escapes the allowed root.");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 && !IsCloud(current))
                    throw new UnsafePathException($"Unsupported link/junction/reparse path: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool IsCloud(string path)
    {
        var nativePath = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        using var handle = CreateFile(nativePath, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out var info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (info.Tag & 0xFFFF0FFFu) == 0x9000001Au;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TagInfo { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out TagInfo info, uint size);
}
