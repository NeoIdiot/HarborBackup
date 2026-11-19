using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HarborBackup.Desktop;

internal static class WindowsNotification
{
    // The normal installer owns the app identity and the fixed, read-only activation command.
    internal const string AppId = "HarborBackup.Desktop";
    internal const string OpenIssuesArgument = "--open-issues";
    internal const string Protocol = "harborbackup-alert";

    internal static bool TryShow(string title, string body)
    {
        try
        {
            using var command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Protocol}\shell\open\command");
            if (Environment.ProcessPath is not { } executable ||
                !string.Equals(command?.GetValue(null) as string, $"\"{executable}\" {OpenIssuesArgument}", StringComparison.OrdinalIgnoreCase))
                return false;

            Check(RoInitialize(1)); // This runs on a worker thread in the multi-threaded apartment.
            try { ShowToast(CreateContent(title, body)); }
            finally { RoUninitialize(); }
            return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or SecurityException)
        {
            Trace.WriteLine($"Windows notification unavailable: {ex.HResult:X8}");
            return false;
        }
    }

    internal static string CreateContent(string title, string body) =>
        new XElement("toast", new XAttribute("activationType", "protocol"),
            new XAttribute("launch", Protocol + ":issues"),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", CleanText(title)), new XElement("text", CleanText(body)))),
            new XElement("audio", new XAttribute("silent", true))).ToString(SaveOptions.DisableFormatting);

    private static string CleanText(string text) => string.Concat(text.EnumerateRunes()
        .Where(r => r.Value > char.MaxValue || XmlConvert.IsXmlChar((char)r.Value)).Take(350).Select(r => r.ToString()));

    // These are the inbox WinRT interfaces, with the six IInspectable slots inherited.
    // Only the members needed for local toast delivery are called.
    private static void ShowToast(string xml)
    {
        using var xmlClass = new HString("Windows.Data.Xml.Dom.XmlDocument");
        Check(RoActivateInstance(xmlClass, out var instance));
        using var document = new Inspectable(instance);
        using var documentIo = document.Query("6cd0e74e-ee65-4489-9ebf-ca43e87ba637"); // IXmlDocumentIO
        using var xmlText = new HString(xml);
        Check(documentIo.Method<OneArgumentMethod>(6)(documentIo.Pointer, xmlText.DangerousGetHandle()));
        using var xmlDocument = document.Query("f7f3a506-1e87-42d6-bcfb-b8c809fa5494"); // IXmlDocument
        using var factory = Factory("Windows.UI.Notifications.ToastNotification", "04124b20-82c6-4229-b109-fd9ed4662b53");
        Check(factory.Method<CreateObject>(6)(factory.Pointer, xmlDocument.Pointer, out var notificationPointer));
        using var notification = new Inspectable(notificationPointer);
        using var manager = Factory("Windows.UI.Notifications.ToastNotificationManager", "50ac103f-d235-4598-bbef-98fe4d1a3ad4");
        using var appId = new HString(AppId);
        Check(manager.Method<CreateObject>(7)(manager.Pointer, appId.DangerousGetHandle(), out var notifierPointer));
        using var notifier = new Inspectable(notifierPointer);
        Check(notifier.Method<ReadSetting>(8)(notifier.Pointer, out var setting));
        if (setting == 0) // Honor Windows notification settings.
            Check(notifier.Method<OneArgumentMethod>(6)(notifier.Pointer, notification.Pointer));
    }

    private static Inspectable Factory(string className, string interfaceId)
    {
        using var name = new HString(className);
        Check(RoGetActivationFactory(name, new Guid(interfaceId), out var pointer));
        return new(pointer);
    }

    private static void Check(int result)
    {
        if (result < 0) throw new COMException("Windows notification call failed.", result);
    }

    private sealed class Inspectable(nint pointer) : IDisposable
    {
        internal nint Pointer => pointer;
        internal T Method<T>(int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), slot * IntPtr.Size));
        internal Inspectable Query(string interfaceId)
        {
            var iid = new Guid(interfaceId);
            Check(Marshal.QueryInterface(pointer, in iid, out var found));
            return new(found);
        }
        public void Dispose() => Marshal.Release(pointer);
    }

    private sealed class HString : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal HString(string text) : base(ownsHandle: true)
        {
            Check(WindowsCreateString(text, (uint)text.Length, out var value));
            SetHandle(value);
        }
        protected override bool ReleaseHandle() => WindowsDeleteString(handle) == 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OneArgumentMethod(nint self, nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateObject(nint self, nint value, out nint created);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadSetting(nint self, out int setting);

    [DllImport("combase.dll")] private static extern int RoInitialize(uint apartment);
    [DllImport("combase.dll")] private static extern void RoUninitialize();
    [DllImport("combase.dll")] private static extern int RoActivateInstance(HString className, out nint instance);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(HString className, in Guid iid, out nint factory);
    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, uint length, out nint text);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint text);
}
