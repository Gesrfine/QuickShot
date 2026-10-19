using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuickShot.Core;
using Shortcut = QuickShot.Core.Shortcut;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly App = Assembly.Load("QuickShot");
    private static readonly List<string> Checks = [];
    private static string _output = "";
    private static object Create(string name, params object?[] args) => Activator.CreateInstance(App.GetType("QuickShot.App." + name)!, Instance, null, args, null)!;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Instance)!.GetValue(value);
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Instance)!.Invoke(value, args);
    private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name, Instance)!.GetValue(value)!;
    private static void Set(object value, string name, object setting) => value.GetType().GetProperty(name, Instance)!.SetValue(value, setting);
    private static uint Sequence() => (uint)App.GetType("QuickShot.App.NativeMethods")!.GetMethod("GetClipboardSequenceNumber", Static)!.Invoke(null, null)!;
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Checks.Add(name); }
    private static string Data()
    {
        var directory = Path.Combine(_output, "data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", directory); return directory;
    }
    private static object Settings(bool enabled)
    {
        var settings = Create("AppSettings"); Set(settings, "ShowNotifications", enabled); return settings;
    }
    private static object LoadSettings() => App.GetType("QuickShot.App.AppSettings")!.GetMethod("Load", Static)!.Invoke(null, null)!;
    private static ApplicationContext Context(bool prepareForms = true)
    {
        var data = Data(); File.WriteAllText(Path.Combine(data, "settings.json"), "{\"ShowNotifications\":false}");
        var context = (ApplicationContext)Create("TrayApplication", (object?)null);
        if (prepareForms) { ((Form)context.GetType().GetProperty("Hint", Instance)!.GetValue(context)!).Opacity = 0; ((Form)context.GetType().GetProperty("Notice", Instance)!.GetValue(context)!).Opacity = 0; }
        Call(context, "UpdateBindings", new JsonArray(JsonSerializer.SerializeToNode(new TargetBinding(Guid.NewGuid().ToString(), "C", "测试目标", "https://example.com/"))), false);
        return context;
    }
    private static void Arm(object context)
    {
        var sequence = Sequence();
        context.GetType().GetField("_lastClipboardSequence", Instance)!.SetValue(context, unchecked(sequence - 1));
        ((CaptureGate)Field(context, "_gate")!).Initialize(unchecked(sequence - 1));
        Call(context, "ProcessClipboard", sequence, true);
    }
    private static object AttachDelivery(object context, string phase)
    {
        var type = context.GetType().GetNestedType("Delivery", BindingFlags.NonPublic)!;
        var delivery = Activator.CreateInstance(type, Instance, null, [new TargetBinding(Guid.NewGuid().ToString(), "C", "测试", "https://example.com/"), Sequence()], null)!;
        type.GetField("Phase", Instance)!.SetValue(delivery, phase);
        context.GetType().GetField("_delivery", Instance)!.SetValue(context, delivery);
        Call(context, "UpdateActivityTimer"); return delivery;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !args.Contains("--background")) return 2;
        _output = Path.GetFullPath(args[0]); Directory.CreateDirectory(_output);
        Environment.SetEnvironmentVariable("QUICKSHOT_INSTANCE_ID", "runtime-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("QUICKSHOT_NO_REGISTER", "1");
        ApplicationConfiguration.Initialize();
        try
        {
            CheckSettings(); CheckKeyboard(); CheckUniversalClipboard(); CheckCommittedDelivery(); CheckHiddenSettings();
            File.WriteAllText(Path.Combine(_output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Runtime checks passed: " + Checks.Count); return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(_output, "result.json"), JsonSerializer.Serialize(new { passed = false, checks = Checks, error = error.ToString() }));
            Console.Error.WriteLine(error); return 1;
        }
    }

    private static void CheckSettings()
    {
        var data = Data(); var path = Path.Combine(data, "settings.json");
        Check(Property<bool>(LoadSettings(), "ShowNotifications"), "notifications default on without creating a settings file");
        Check(!File.Exists(path), "defaults do not create unused configuration files");
        Check(App.GetType("QuickShot.App.AppSettings")!.GetProperty("CaptureShortcut") == null, "2.0 has no dedicated screenshot shortcut setting");
        Call(Settings(false), "Save");
        Check(!Property<bool>(LoadSettings(), "ShowNotifications"), "settings persist and reload");
        var original = File.ReadAllText(path);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { Call(Settings(true), "Save"); throw new InvalidOperationException("locked settings were replaced"); }
            catch (TargetInvocationException error) when (error.InnerException is IOException or UnauthorizedAccessException) { Check(true, "locked settings reject atomic replacement"); }
        }
        Check(File.ReadAllText(path) == original && !Directory.EnumerateFiles(data, ".settings-*.tmp").Any(), "failed save preserves the original and removes temporary files");
        foreach (var invalid in new[] { "{", "null", "{\"ShowNotifications\":\"bad\"}" })
        {
            File.WriteAllText(path, invalid);
            Check(Property<bool>(LoadSettings(), "ShowNotifications"), "damaged settings fall back without crashing: " + invalid);
        }
        File.WriteAllText(path, "{\"ShowNotifications\":false,\"CaptureShortcut\":\"Alt+Shift+D\"}");
        Check(!Property<bool>(LoadSettings(), "ShowNotifications"), "legacy screenshot fields do not affect the new program");
    }

    private static void CheckKeyboard()
    {
        using var keyboard = (IDisposable)Create("KeyboardMonitor");
        Check(!Property<bool>(keyboard, "Enabled"), "idle keyboard monitor installs no hook");
        Set(keyboard, "Enabled", true);
        var dataType = App.GetType("QuickShot.App.NativeMethods+KeyboardHookData")!;
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(dataType));
        var strokes = new List<KeyStroke>();
        Set(keyboard, "KeyInput", new Func<KeyStroke, bool>(stroke => { strokes.Add(stroke); return false; }));
        try
        {
            nint Key(uint code, bool down, uint flags = 0)
            {
                var data = Activator.CreateInstance(dataType)!;
                dataType.GetField("VkCode", Instance)!.SetValue(data, code);
                dataType.GetField("Flags", Instance)!.SetValue(data, flags);
                Marshal.StructureToPtr(data, pointer, false);
                return (nint)Call(keyboard, "Callback", 0, (nint)(down ? 0x100 : 0x101), pointer)!;
            }
            for (uint key = 7; key < 255; key++) Key(key, false);
            Key(0xA2, true); Key('C', true);
            Check(strokes.Count == 0, "inactive delivery consumer leaves ordinary input alone");
            Call(keyboard, "MarkImageReady"); Set(keyboard, "KeyInputEnabled", true);
            Key('C', false); Key(0xA2, false);
            Check(strokes.All(stroke => stroke.HeldAtCapture && !stroke.IsDown), "copy shortcut releases are marked as held before the image");
            Check(strokes.All(stroke => ShortcutMatcher.Evaluate([Shortcut.Parse("C")], stroke) == ShortcutDecision.Ignore), "Ctrl+C release cannot cancel eligibility");
            strokes.Clear();
            Key(0xA2, true); Key(0xA3, true); Key(0xA2, false); Key('D', true);
            Check(strokes[^1].Modifiers == ShortcutModifiers.Ctrl, "right Ctrl remains active after releasing left Ctrl");
            Key('D', false); Key(0xA3, false);
            Check(strokes[^1].Modifiers == ShortcutModifiers.None, "modifiers clear after both sides release");
            var count = strokes.Count; Key('C', true, 0x10); Key('C', false, 0x10);
            Check(strokes.Count == count, "injected keystrokes are ignored");
            Set(keyboard, "KeyInput", new Func<KeyStroke, bool>(stroke => stroke.VirtualKey == 'C' && stroke.IsDown));
            Check(Key('C', true) == 1 && Key('C', true) == 1, "a matched key and its repeats are suppressed once");
            Set(keyboard, "Enabled", false);
            Check(Property<bool>(keyboard, "Enabled"), "pending suppressed release retains the hook until balanced");
            Check(Key('C', false) == 1 && !Property<bool>(keyboard, "Enabled"), "release is swallowed and the idle hook is removed");
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void CheckUniversalClipboard()
    {
        using (var lean = Context(false))
        {
            Check(Field(lean, "_hint") == null && Field(lean, "_notice") == null, "idle tray does not construct unused notification windows");
            Arm(lean);
            Check(Field(lean, "_hint") == null && Field(lean, "_notice") == null, "hidden notifications stay unallocated when an image arrives");
        }
        using var context = Context();
        var gate = (CaptureGate)Field(context, "_gate")!;
        var keyboard = Field(context, "_keyboard")!;
        var timer = (System.Windows.Forms.Timer)Field(context, "_timer")!;
        Check(!Property<bool>(keyboard, "Enabled") && !timer.Enabled, "idle tray has no keyboard hook or polling timer");
        var notify = context.GetType().GetMethod("ClipboardChanged", Instance)!.CreateDelegate<Action>(context);
        for (var i = 0; i < 10000; i++) notify();
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) notify();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Check(allocated == 0, "duplicate clipboard notifications allocate zero managed bytes");
        Console.WriteLine($"Duplicate clipboard callbacks: {allocated} managed bytes / 10000 events");
        Check(gate.Phase == CapturePhase.Idle, "startup clipboard and duplicate callbacks never arm");
        Arm(context);
        Check(gate.Phase == CapturePhase.Armed && Property<bool>(keyboard, "Enabled") && timer.Enabled, "any source's new image enables the ten-second delivery window");
        Check(!((Form)Field(context, "_hint")!).Visible, "hidden notifications do not prevent arming");
        var deadline = gate.Deadline; Call(context, "ProcessClipboard", Sequence(), true);
        Check(gate.Deadline == deadline, "duplicate image events do not reset the countdown");
        Call(context, "ConsumeKey", new KeyStroke(0xA2, ShortcutModifiers.None, false, false, true));
        Check(gate.Phase == CapturePhase.Armed, "releasing the copy shortcut preserves eligibility");
        Call(context, "ConsumeKey", new KeyStroke('A', ShortcutModifiers.None, true));
        Check(gate.Phase == CapturePhase.Idle && !timer.Enabled && !Property<bool>(keyboard, "Enabled"), "ordinary input cancels and immediately stops idle monitoring");
        Arm(context); Call(context, "ConsumeKey", new KeyStroke(0x1B, ShortcutModifiers.None, true));
        Check(gate.Phase == CapturePhase.Idle, "Escape cancels before delivery");
        Arm(context); ((Action)Property<Delegate>(Field(context, "_mouse")!, "Clicked"))();
        Check(gate.Phase == CapturePhase.Idle, "mouse clicks cancel before delivery");
        Arm(context); Set(gate, "Deadline", DateTimeOffset.UtcNow.AddMilliseconds(-1)); Call(context, "Tick");
        Check(gate.Phase == CapturePhase.Idle && !timer.Enabled && !Property<bool>(keyboard, "Enabled"), "timeout hides the hint and stops monitoring");
        Arm(context); Call(context, "ProcessClipboard", unchecked(Sequence() + 1), false);
        Check(gate.Phase == CapturePhase.Idle, "text or file replacement cancels the armed image");
        Arm(context);
        Check((bool)Call(context, "ConsumeKey", new KeyStroke('C', ShortcutModifiers.None, true))!, "configured target key commits delivery");
        var delivery = Field(context, "_delivery");
        Check(delivery != null && gate.Phase == CapturePhase.Idle, "eligibility is consumed when delivery is committed");
        Check(!(bool)Call(context, "ConsumeKey", new KeyStroke('C', ShortcutModifiers.None, true))! && ReferenceEquals(delivery, Field(context, "_delivery")), "repeated shortcut cannot create a second delivery");
        Call(context, "CancelDelivery", false);
    }

    private static void CheckCommittedDelivery()
    {
        using var context = Context();
        foreach (var phase in new[] { "connecting", "preparing", "waitingForActivation", "verifying", "focusingContent", "waitingForKeys", "pasting" })
        {
            var delivery = AttachDelivery(context, phase);
            Check(!(bool)Call(context, "ConsumeKey", new KeyStroke('A', ShortcutModifiers.None, true))! && ReferenceEquals(delivery, Field(context, "_delivery")), "committed " + phase + ": ordinary typing does not cancel");
            Check(!(bool)Call(context, "ConsumeKey", new KeyStroke(0x1B, ShortcutModifiers.None, true))! && ReferenceEquals(delivery, Field(context, "_delivery")), "committed " + phase + ": Escape does not cancel");
            ((Action)Property<Delegate>(Field(context, "_mouse")!, "Clicked"))();
            Check(ReferenceEquals(delivery, Field(context, "_delivery")), "committed " + phase + ": mouse clicks do not cancel");
            Check(!Property<bool>(Field(context, "_mouse")!, "Enabled"), "committed " + phase + ": mouse hook is disabled");
            Call(context, "CancelDelivery", false);
        }
        var held = AttachDelivery(context, "verifying");
        var suppressed = (HashSet<uint>)Field(Field(context, "_keyboard")!, "_suppressed")!;
        suppressed.Add('C'); Call(context, "HandlePasteNow");
        Check((string)Field(held, "Phase")! == "waitingForKeys", "paste waits for the shortcut release");
        Call(context, "Tick");
        Check((string)Field(held, "Phase")! == "waitingForKeys", "held keys cannot trigger a paste");
        suppressed.Clear(); Call(context, "Tick");
        Check((string)Field(held, "Phase")! == "verifying", "released keys require fresh focus verification");
        held.GetType().GetField("Phase", Instance)!.SetValue(held, "pasting"); Call(context, "HandlePasteNow");
        Check((string)Field(held, "Phase")! == "pasting", "duplicate paste permission after dispatch is ignored");
        Call(context, "CancelDelivery", false);
        AttachDelivery(context, "connecting");
        Call(context, "ProcessClipboard", unchecked(Sequence() + 1), true);
        Check(Field(context, "_delivery") == null && ((CaptureGate)Field(context, "_gate")!).Phase == CapturePhase.Idle, "clipboard change during delivery stops without automatically sending a new image");
    }

    private static void CheckHiddenSettings()
    {
        using var context = Context(); var data = Environment.GetEnvironmentVariable("QUICKSHOT_DATA_DIR")!;
        Arm(context); var gate = (CaptureGate)Field(context, "_gate")!;
        Check(Call(context, "TryApplySettings", Settings(true)) == null && gate.Phase == CapturePhase.Armed, "showing hints does not change eligibility");
        Check(((Form)Field(context, "_hint")!).Visible, "enabling notifications shows the remaining countdown");
        Call(context, "Notify", "测试结果");
        Check(((Form)Field(context, "_notice")!).Visible, "enabled results are shown without activation");
        Check(Call(context, "TryApplySettings", Settings(false)) == null, "notification setting saves successfully");
        Check(!((Form)Field(context, "_hint")!).Visible && !((Form)Field(context, "_notice")!).Visible, "disabling notifications immediately hides both windows");
        Call(context, "Tick"); Call(context, "Notify", "隐藏后的结果");
        Check(!((Form)Field(context, "_notice")!).Visible && (string)Field(context, "_status")! == "隐藏后的结果", "hidden delivery results remain available in the dashboard");
        using (File.Open(Path.Combine(data, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Check(Call(context, "TryApplySettings", Settings(true)) is string && !Property<bool>(Field(context, "_settings")!, "ShowNotifications"), "failed settings save leaves the running configuration unchanged");
        using var restarted = (ApplicationContext)Create("TrayApplication", (object?)null);
        Check(!Property<bool>(Field(restarted, "_settings")!, "ShowNotifications"), "restart restores the hidden-notification preference");
    }
}
