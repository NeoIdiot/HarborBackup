using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace HarborBackup.Desktop.Localization;

// Presentation-only resources. Page keys, JSON fields, paths and log formats stay language-neutral.
internal static class UiText
{
    private static readonly ResourceManager Resources = new("HarborBackup.Desktop.Localization.Strings", typeof(UiText).Assembly);
    private static readonly ConditionalWeakTable<Control, Binding> Bindings = new();
    internal static event Action? Changed;
    internal static string Language { get; private set; } = "zh-CN";
    internal static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    internal static string Normalize(string? language) => language == "en-US" ? "en-US" : "zh-CN";
    internal static string Text(string key, params object?[] args)
    {
        var value = Resources.GetString(key, Culture) ?? key;
        return args.Length == 0 ? value : string.Format(Culture, value, args);
    }
    internal static void SetLanguage(string language)
    {
        var normalized = Normalize(language);
        if (Language == normalized) return;
        Language = normalized;
        Changed?.Invoke();
    }
    internal static T Bind<T>(T control, string key) where T : Control
    {
        Set(control, key);
        return control;
    }
    internal static void Set(Control control, string key, params object?[] args)
    {
        if (!Bindings.TryGetValue(control, out var binding))
        {
            binding = new Binding(control);
            Bindings.Add(control, binding);
        }
        binding.Key = key; binding.Arguments = args; binding.Apply();
    }
    internal static void Watch(Control owner, Action update)
    {
        Changed += update;
        owner.Disposed += (_, _) => Changed -= update;
        update();
    }
    internal static ToolStripItem Bind(ToolStripItem item, string key)
    {
        void Update() { item.Text = Text(key); item.AccessibleName = item.Text; }
        Changed += Update; item.Disposed += (_, _) => Changed -= Update; Update(); return item;
    }
    private sealed class Binding
    {
        private readonly Control control;
        internal string Key { get; set; } = "";
        internal object?[] Arguments { get; set; } = [];
        internal Binding(Control control)
        {
            this.control = control;
            Changed += Apply;
            control.Disposed += (_, _) => { Changed -= Apply; Bindings.Remove(control); };
        }
        internal void Apply()
        {
            if (control.IsDisposed) return;
            var text = Text(Key, Arguments);
            if (control.Text != text) control.Text = text;
            if (control is Button or CheckBox) control.AccessibleName = control.Text;
        }
    }
}
