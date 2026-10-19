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

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !args.Contains("--background"))
        {
            Console.Error.WriteLine("Usage: QuickShot.Runtime.Tests <dedicated-output-directory> --background");
            return 2;
        }
        var output = Path.GetFullPath(args[0]);
        _output = output;
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("QUICKSHOT_INSTANCE_ID", "runtime-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", Path.Combine(output, "data"));
        Environment.SetEnvironmentVariable("QUICKSHOT_NO_REGISTER", "1");
        ApplicationConfiguration.Initialize();
        try
        {
            CheckAppSettings();
            CheckKeyboard();
            CheckActivityAndClipboard();
            CheckExpiredHint();
            CheckCommittedDelivery();
            CheckHeldDeliveryKeys();
            CheckRuntimeSettings();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Runtime event checks passed: {Checks.Count}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = false, checks = Checks, error = exception.ToString() }));
            return 1;
        }
    }

    private static object Create(string name, params object?[] parameters) => Activator.CreateInstance(App.GetType("QuickShot.App." + name)!, Instance, null, parameters, null)!;
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Instance)!.GetValue(owner);
    private static void Invoke(object owner, string name, params object?[] parameters) => owner.GetType().GetMethod(name, Instance)!.Invoke(owner, parameters);
    private static void Set(object owner, string name, object value) => owner.GetType().GetProperty(name, Instance)!.SetValue(owner, value);
    private static bool InputEnabled(object keyboard) => (bool)keyboard.GetType().GetProperty("KeyInputEnabled", Instance)!.GetValue(keyboard)!;
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Checks.Add(name); }

    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;
    private static Type SettingsType => App.GetType("QuickShot.App.AppSettings")!;
    private static object LoadSettings() => SettingsType.GetMethod("Load", Static)!.Invoke(null, null)!;
    private static Shortcut ParseCaptureShortcut(string text) => (Shortcut)SettingsType.GetMethod("ParseCaptureShortcut", Static)!.Invoke(null, [text])!;
    private static object Settings(bool show, string shortcut)
    {
        var settings = Create("AppSettings");
        Set(settings, "ShowNotifications", show);
        Set(settings, "CaptureShortcut", shortcut);
        return settings;
    }
    private static string? ApplySettings(object context, object settings) => (string?)context.GetType().GetMethod("TryApplySettings", Instance)!.Invoke(context, [settings]);
    private static Exception Failure(Action action)
    {
        try { action(); }
        catch (TargetInvocationException error) when (error.InnerException != null) { return error.InnerException; }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("The expected failure did not occur.");
    }
    private static void WithSettingsDirectory(string name, Action<string> action)
    {
        var original = Environment.GetEnvironmentVariable("QUICKSHOT_DATA_DIR");
        var data = Path.Combine(_output, name + "-" + Guid.NewGuid().ToString("N"), "data");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", data);
        try { action(data); }
        finally { Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", original); }
    }

    private static void CheckAppSettings()
    {
        WithSettingsDirectory("settings", data =>
        {
            var path = Path.Combine(data, "settings.json");
            var defaults = LoadSettings();
            Check(Property<bool>(defaults, "ShowNotifications") && Property<string>(defaults, "CaptureShortcut") == "Alt+Shift+D",
                "missing settings preserve notification and capture shortcut defaults");
            Check(!File.Exists(path), "reading defaults does not create a configuration file");
            Check(ParseCaptureShortcut("shift+control+s").Text == "Ctrl+Shift+S", "capture shortcuts normalize aliases and modifier order");
            Check(ParseCaptureShortcut("PrintScreen") is { VirtualKey: 0x2C, Modifiers: ShortcutModifiers.None }, "PrintScreen works as a standalone capture shortcut");
            Check(ParseCaptureShortcut("F12") is { VirtualKey: 0x7B, Modifiers: ShortcutModifiers.None }, "function keys work as standalone capture shortcuts");
            Check(ParseCaptureShortcut("Ctrl+PrintScreen").Text == "Ctrl+PrintScreen", "modified PrintScreen capture shortcut is supported");
            foreach (var shortcut in new[] { "D", "7", "Enter", "Esc", "Ctrl+Esc", "Ctrl+Alt+Shift+D", "Ctrl+Ctrl+D", "Ctrl+Alt" })
                Check(Failure(() => ParseCaptureShortcut(shortcut)) is FormatException, $"unsafe or invalid capture shortcut is rejected: {shortcut}");

            var selected = Settings(false, "shift+ctrl+s");
            Invoke(selected, "Save");
            var loaded = LoadSettings();
            Check(!Property<bool>(loaded, "ShowNotifications") && Property<string>(loaded, "CaptureShortcut") == "Ctrl+Shift+S",
                "notification preference and normalized capture shortcut survive a settings round trip");
            Check(Directory.GetFiles(data, ".settings-*.tmp").Length == 0, "successful settings write leaves no temporary files");
            var previous = File.ReadAllText(path);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(Failure(() => Invoke(Settings(true, "F12"), "Save")) is IOException or UnauthorizedAccessException,
                    "locked settings replacement reports a write failure");
                Check(File.ReadAllText(path) == previous, "failed atomic replacement preserves the previous configuration");
            }
            Check(Directory.GetFiles(data, ".settings-*.tmp").Length == 0, "failed settings write removes its temporary file");

            foreach (var malformed in new[] { "{", "null", "{\"ShowNotifications\":\"false\"}", "{\"CaptureShortcut\":null}", "{\"CaptureShortcut\":\"D\"}" })
            {
                File.WriteAllText(path, malformed);
                var recovered = LoadSettings();
                Check(Property<bool>(recovered, "ShowNotifications") && Property<string>(recovered, "CaptureShortcut") == "Alt+Shift+D",
                    "malformed settings fall back to safe defaults: " + malformed);
            }
            Check(File.ReadAllText(Path.Combine(data, "diagnostics.log")).Contains("[settings]", StringComparison.Ordinal),
                "settings recovery retains a diagnostic entry");
        });
    }

    private static void CheckKeyboard()
    {
        using var monitor = (IDisposable)Create("KeyboardMonitor");
        var strokes = new List<KeyStroke>();
        var captures = 0;
        Set(monitor, "KeyInput", new Func<KeyStroke, bool>(stroke => { strokes.Add(stroke); return stroke.VirtualKey == 'C' && stroke.IsDown; }));
        Set(monitor, "CaptureStarted", new Action(() => captures++));
        var dataType = App.GetType("QuickShot.App.NativeMethods+KeyboardHookData")!;
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(dataType));
        try
        {
            // Exercise the hook callback directly: no SendInput, OS keystrokes or clipboard writes.
            nint Key(uint key, bool down, uint flags = 0, uint scan = 0)
            {
                var data = Activator.CreateInstance(dataType)!;
                dataType.GetField("VkCode", Instance)!.SetValue(data, key);
                dataType.GetField("ScanCode", Instance)!.SetValue(data, scan);
                dataType.GetField("Flags", Instance)!.SetValue(data, flags);
                Marshal.StructureToPtr(data, pointer, false);
                return (nint)monitor.GetType().GetMethod("Callback", Instance)!.Invoke(monitor, [0, (nint)(down ? 0x100 : 0x101), pointer])!;
            }
            for (uint key = 7; key < 255; key++) Key(key, false);
            strokes.Clear();
            Key('A', true); Key('A', false);
            Check(strokes.Count == 0, "idle typing bypasses delivery consumer");
            Key(0x12, true); Key(0x10, true); Key('D', true); Key('D', true);
            Check(captures == 1, "capture chord remains active while delivery consumer sleeps and ignores repeats");
            Key('D', false); Key(0x10, false); Key(0x12, false);
            Set(monitor, "KeyInputEnabled", true);
            Key(0x11, true); Key(0x11, true, 1); Key(0x11, false);
            Key(0x12, true); Key('C', true);
            Check(strokes[^1] is { VirtualKey: 'C', Modifiers: ShortcutModifiers.Ctrl | ShortcutModifiers.Alt }, "right Ctrl remains active after left Ctrl release");
            Check(Key('C', false) == 1, "consumed shortcut release remains suppressed");
            Key(0x12, false); Key(0x11, false, 1); Key('B', true); Key('B', false);
            Check(strokes[^1].Modifiers == ShortcutModifiers.None, "modifier mask clears after both sides release");
            Key(0x10, true); Invoke(monitor, "MarkScreenshotReady"); Key(0x10, false);
            Check(strokes[^1] is { IsDown: false, HeldAtCapture: true, Modifiers: ShortcutModifiers.None }, "screenshot chord release retains held-at-capture marker");
            Key(0x12, true); Invoke(monitor, "MarkScreenshotReady"); Key(0x12, false);
            Check(strokes[^1].HeldAtCapture, "held-key snapshot can be reused for another screenshot");
            var count = strokes.Count;
            Key('A', true, 0x10); Key('A', false, 0x10);
            Check(strokes.Count == count, "injected keyboard input remains ignored");

            Set(monitor, "KeyInputEnabled", false);
            var custom = ParseCaptureShortcut("Ctrl+Shift+S");
            Set(monitor, "CaptureShortcut", custom);
            var before = captures;
            Key(0x12, true); Key(0x10, true); Key('D', true); Key('D', false); Key(0x10, false); Key(0x12, false);
            Check(captures == before, "changing the capture shortcut disables the previous Alt+Shift+D chord");
            Key(0x11, true); Key(0x10, true);
            var passed = Key('S', true);
            Key('S', true);
            Check(captures == before + 1, "custom capture shortcut fires exactly once while its primary key is held");
            Check(passed != 1 && !((HashSet<uint>)Field(monitor, "_suppressed")!).Contains('S'),
                "custom capture shortcut passes through to FastStone without suppression");
            Key('S', false);
            Key('S', true, 0x10); Key('S', false, 0x10);
            Check(captures == before + 1, "injected custom capture shortcut does not arm delivery");
            Key(0x10, false); Key(0x11, false);
            foreach (var shortcut in new[] { "PrintScreen", "F12" })
            {
                var captureShortcut = ParseCaptureShortcut(shortcut);
                Set(monitor, "CaptureShortcut", captureShortcut);
                before = captures;
                passed = Key(captureShortcut.VirtualKey, true);
                Key(captureShortcut.VirtualKey, true);
                Key(captureShortcut.VirtualKey, false);
                Check(captures == before + 1 && passed != 1, $"standalone {shortcut} arms once and passes through to FastStone");
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static void CheckActivityAndClipboard()
    {
        using var context = (ApplicationContext)Create("TrayApplication", (object?)null);
        var gate = (CaptureGate)Field(context, "_gate")!;
        var keyboard = Field(context, "_keyboard")!;
        var timer = (System.Windows.Forms.Timer)Field(context, "_timer")!;
        Check(!InputEnabled(keyboard) && !timer.Enabled, "idle state disables delivery-key consumer and polling");
        var clipboard = context.GetType().GetMethod("ClipboardChanged", Instance)!.CreateDelegate<Action>(context);
        for (var index = 0; index < 10000; index++) clipboard();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10000; index++) clipboard();
        var clipboardBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Console.WriteLine($"Idle clipboard callbacks: {clipboardBytes} managed bytes / 10000 events");
        Check(clipboardBytes == 0, "idle clipboard notifications allocate no managed memory");
        var native = App.GetType("QuickShot.App.NativeMethods")!;
        var sequence = (uint)native.GetMethod("GetClipboardSequenceNumber", Static)!.Invoke(null, null)!;
        gate.CaptureStarted(sequence, DateTimeOffset.UtcNow);
        Invoke(context, "UpdateActivityTimer");
        Check(InputEnabled(keyboard), "capture selection still handles Escape cancellation");
        clipboard();
        Check(gate.Phase == CapturePhase.WaitingForImage, "unchanged clipboard never arms a new screenshot");
        gate.ClipboardChanged(unchecked(sequence - 1), true, true, DateTimeOffset.UtcNow);
        Invoke(context, "UpdateActivityTimer");
        Check(InputEnabled(keyboard) && gate.Phase == CapturePhase.Armed, "new screenshot enables delivery keys");
        clipboard();
        Check(gate.Phase == CapturePhase.Idle && !InputEnabled(keyboard) && !timer.Enabled, "replaced armed clipboard cancels delivery and returns to idle");
        gate.CaptureStarted(sequence, DateTimeOffset.UtcNow);
        Invoke(context, "UpdateActivityTimer");
        Invoke(context, "ConsumeKey", new KeyStroke(0x1B, ShortcutModifiers.None, true));
        Check(gate.Phase == CapturePhase.Idle && !InputEnabled(keyboard), "Escape during screenshot selection still cancels pending capture");
    }

    private static void CheckExpiredHint()
    {
        using var context = (ApplicationContext)Create("TrayApplication", (object?)null);
        var gate = (CaptureGate)Field(context, "_gate")!;
        var hint = (Form)Field(context, "_hint")!;
        hint.Opacity = 0;
        var target = new TargetBinding(Guid.NewGuid().ToString(), "C", "测试接收目标", "https://example.com/");
        Invoke(hint, "UpdateHint", new[] { target }, 10);
        Check(hint.Visible, "expired-shortcut fixture starts with visible screenshot hint");
        var native = App.GetType("QuickShot.App.NativeMethods")!;
        var sequence = (uint)native.GetMethod("GetClipboardSequenceNumber", Static)!.Invoke(null, null)!;
        var captured = DateTimeOffset.UtcNow - CaptureGate.ArmedDuration - TimeSpan.FromSeconds(1);
        gate.CaptureStarted(unchecked(sequence - 1), captured);
        gate.ClipboardChanged(sequence, true, true, captured);
        Invoke(context, "UpdateActivityTimer");
        Invoke(context, "ConsumeKey", new KeyStroke('C', ShortcutModifiers.None, true));
        Check(gate.Phase == CapturePhase.Idle, "shortcut arriving after expiry is rejected");
        Check(!hint.Visible, "expired shortcut removes hint even when the countdown timer has stopped");
        Invoke(context, "ClipboardChanged");
        Check(!hint.Visible, "following idle clipboard notification leaves the hint hidden");
    }

    private static object AttachDelivery(object context, uint sequence, string phase)
    {
        var type = context.GetType().GetNestedType("Delivery", BindingFlags.NonPublic)!;
        var delivery = Activator.CreateInstance(type, Instance, null,
            [new TargetBinding(Guid.NewGuid().ToString(), "C", "测试接收目标", "https://example.com/"), sequence], null)!;
        type.GetField("Phase", Instance)!.SetValue(delivery, phase);
        context.GetType().GetField("_delivery", Instance)!.SetValue(context, delivery);
        Invoke(context, "UpdateActivityTimer");
        return delivery;
    }

    private static uint ClipboardSequence() => (uint)App.GetType("QuickShot.App.NativeMethods")!.GetMethod("GetClipboardSequenceNumber", Static)!.Invoke(null, null)!;

    private static void CheckCommittedDelivery()
    {
        using var context = (ApplicationContext)Create("TrayApplication", (object?)null);
        var gate = (CaptureGate)Field(context, "_gate")!;
        var keyboard = Field(context, "_keyboard")!;
        var mouse = Field(context, "_mouse")!;
        var sequence = ClipboardSequence();
        var consume = context.GetType().GetMethod("ConsumeKey", Instance)!.CreateDelegate<Func<KeyStroke, bool>>(context);
        var capture = (Action)keyboard.GetType().GetProperty("CaptureStarted", Instance)!.GetValue(keyboard)!;
        var click = (Action)mouse.GetType().GetProperty("Clicked", Instance)!.GetValue(mouse)!;
        foreach (var phase in new[] { "connecting", "preparing", "waitingForActivation", "verifying", "focusingContent", "waitingForKeys", "pasting" })
        {
            var delivery = AttachDelivery(context, sequence, phase);
            Check(!InputEnabled(keyboard) && !(bool)mouse.GetType().GetProperty("Enabled", Instance)!.GetValue(mouse)!, $"committed {phase}: delivery cancellation hooks are inactive");
            Check(!consume(new KeyStroke('A', ShortcutModifiers.None, true)) && ReferenceEquals(Field(context, "_delivery"), delivery), $"committed {phase}: ordinary typing passes through without cancelling");
            Check(!consume(new KeyStroke(0x1B, ShortcutModifiers.None, true)) && ReferenceEquals(Field(context, "_delivery"), delivery), $"committed {phase}: Escape passes through without cancelling");
            click();
            Check(ReferenceEquals(Field(context, "_delivery"), delivery), $"committed {phase}: mouse click does not cancel");
            capture();
            Check(ReferenceEquals(Field(context, "_delivery"), delivery) && gate.Phase == CapturePhase.Idle, $"committed {phase}: another capture chord does not cancel or rearm");
            Invoke(context, "CancelDelivery", false);
        }
        foreach (var input in new[] { new KeyStroke('A', ShortcutModifiers.None, true), new KeyStroke(0x1B, ShortcutModifiers.None, true) })
        {
            gate.CaptureStarted(unchecked(sequence - 1), DateTimeOffset.UtcNow);
            gate.ClipboardChanged(sequence, true, true, DateTimeOffset.UtcNow);
            Invoke(context, "UpdateActivityTimer");
            Check(!consume(input) && gate.Phase == CapturePhase.Idle, $"before shortcut: key {input.VirtualKey} cancels screenshot without swallowing input");
        }
        gate.CaptureStarted(unchecked(sequence - 1), DateTimeOffset.UtcNow);
        gate.ClipboardChanged(sequence, true, true, DateTimeOffset.UtcNow);
        Invoke(context, "UpdateActivityTimer");
        click();
        Check(gate.Phase == CapturePhase.Idle, "before shortcut: mouse click still cancels screenshot");
        var target = new TargetBinding(Guid.NewGuid().ToString(), "C", "测试接收目标", "https://example.com/");
        Invoke(context, "UpdateBindings", new JsonArray(JsonSerializer.SerializeToNode(target)), false);
        gate.CaptureStarted(unchecked(sequence - 1), DateTimeOffset.UtcNow);
        gate.ClipboardChanged(sequence, true, true, DateTimeOffset.UtcNow);
        Invoke(context, "UpdateActivityTimer");
        Check(consume(new KeyStroke('C', ShortcutModifiers.None, true)) && gate.Phase == CapturePhase.Idle && Field(context, "_delivery") != null,
            "matching shortcut atomically commits delivery and consumes its trigger");
        var committed = Field(context, "_delivery");
        Check(!consume(new KeyStroke('B', ShortcutModifiers.None, true)) && ReferenceEquals(Field(context, "_delivery"), committed),
            "typing immediately after a valid shortcut preserves the queued delivery");
        Invoke(context, "CancelDelivery", false);
        AttachDelivery(context, unchecked(sequence - 1), "waitingForKeys");
        ((Form)Field(context, "_notice")!).Opacity = 0;
        Invoke(context, "ClipboardChanged");
        Check(Field(context, "_delivery") == null, "committed delivery still stops when clipboard sequence is replaced");
    }

    private static void CheckHeldDeliveryKeys()
    {
        using var context = (ApplicationContext)Create("TrayApplication", (object?)null);
        var keyboard = Field(context, "_keyboard")!;
        var suppressed = (HashSet<uint>)Field(keyboard, "_suppressed")!;
        var delivery = AttachDelivery(context, ClipboardSequence(), "verifying");
        suppressed.Add('C');
        Invoke(context, "HandlePasteNow");
        Check(ReferenceEquals(Field(context, "_delivery"), delivery) && (string)Field(delivery, "Phase")! == "waitingForKeys", "paste permission waits for held delivery keys instead of failing");
        Invoke(context, "Tick");
        Check((string)Field(delivery, "Phase")! == "waitingForKeys", "held keys never trigger paste or repeated focus verification");
        suppressed.Clear();
        Invoke(context, "Tick");
        Check((string)Field(delivery, "Phase")! == "verifying", "key release requests a complete new focus verification before paste");
        delivery.GetType().GetField("Phase", Instance)!.SetValue(delivery, "pasting");
        Invoke(context, "HandlePasteNow");
        Check(ReferenceEquals(Field(context, "_delivery"), delivery) && (string)Field(delivery, "Phase")! == "pasting", "duplicate paste permission after dispatch is ignored");
    }

    private static void CheckRuntimeSettings()
    {
        WithSettingsDirectory("runtime-settings", data =>
        {
            using (var context = (ApplicationContext)Create("TrayApplication", (object?)null))
            {
                var gate = (CaptureGate)Field(context, "_gate")!;
                var keyboard = Field(context, "_keyboard")!;
                var mouse = Field(context, "_mouse")!;
                var hint = (Form)Field(context, "_hint")!;
                var notice = (Form)Field(context, "_notice")!;
                hint.Opacity = notice.Opacity = 0;
                var targets = new[]
                {
                    new TargetBinding(Guid.NewGuid().ToString(), "C", "测试接收目标", "https://example.com/"),
                    new TargetBinding(Guid.NewGuid().ToString(), "Ctrl+Alt+C", "组合键接收目标", "https://example.org/")
                };
                Invoke(context, "UpdateBindings", new JsonArray(targets.Select(target => JsonSerializer.SerializeToNode(target)).ToArray()), false);
                var sequence = ClipboardSequence();
                void Arm(DateTimeOffset? captured = null)
                {
                    var time = captured ?? DateTimeOffset.UtcNow;
                    gate.CaptureStarted(unchecked(sequence - 1), time);
                    gate.ClipboardChanged(sequence, true, true, time);
                    Invoke(context, "UpdateActivityTimer");
                }
                Arm();
                Invoke(hint, "UpdateHint", targets, 10);
                Invoke(context, "Notify", "settings visibility fixture");
                Check(hint.Visible && notice.Visible, "notification preference fixture starts with visible hint and result notice");
                Check(ApplySettings(context, Settings(false, "Alt+Shift+D")) == null,
                    "notification preference can be changed without changing the capture shortcut");
                Check(!hint.Visible && !notice.Visible && gate.Phase == CapturePhase.Armed && InputEnabled(keyboard),
                    "turning off notifications hides both windows immediately and preserves the armed screenshot");
                Invoke(context, "Tick");
                Check(!hint.Visible && !notice.Visible && gate.Phase == CapturePhase.Armed,
                    "countdown updates do not redisplay disabled notifications");
                Invoke(context, "Notify", "hidden delivery result");
                Check(!notice.Visible && (string)Field(context, "_status")! == "hidden delivery result",
                    "hidden notifications still update the dashboard status");
                var consume = context.GetType().GetMethod("ConsumeKey", Instance)!.CreateDelegate<Func<KeyStroke, bool>>(context);
                Check(consume(new KeyStroke('C', ShortcutModifiers.None, true)) && Field(context, "_delivery") != null,
                    "delivery shortcuts still commit screenshots while notifications are hidden");
                Invoke(context, "CancelDelivery", false);
                Arm();
                Check(!consume(new KeyStroke('A', ShortcutModifiers.None, true)) && gate.Phase == CapturePhase.Idle,
                    "unrelated typing still cancels an armed screenshot with notifications hidden");
                Arm();
                ((Action)mouse.GetType().GetProperty("Clicked", Instance)!.GetValue(mouse)!)();
                Check(gate.Phase == CapturePhase.Idle, "mouse activity still cancels an armed screenshot with notifications hidden");
                Arm(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(11));
                Invoke(context, "Tick");
                Check(gate.Phase == CapturePhase.Idle && !InputEnabled(keyboard) && !hint.Visible,
                    "hidden screenshot eligibility still expires after ten seconds");

                Check(ApplySettings(context, Settings(true, "Alt+Shift+D")) == null, "notifications can be enabled again");
                Arm();
                Invoke(context, "Tick");
                Check(hint.Visible, "re-enabling notifications restores the live screenshot hint");
                Check(ApplySettings(context, Settings(false, "control+alt+s")) == null,
                    "a valid custom capture shortcut can be applied and normalized");
                Check(gate.Phase == CapturePhase.Idle && Property<Shortcut>(keyboard, "CaptureShortcut").Text == "Ctrl+Alt+S"
                    && Property<string>(Field(context, "_settings")!, "CaptureShortcut") == "Ctrl+Alt+S",
                    "changing capture shortcut cancels uncommitted eligibility and updates the running monitor");
                var settingsPath = Path.Combine(data, "settings.json");
                var saved = File.ReadAllText(settingsPath);
                Check(ApplySettings(context, Settings(true, "Ctrl+Alt+C")) is { Length: > 0 },
                    "capture shortcut conflicts with an existing delivery binding are rejected");
                Check(File.ReadAllText(settingsPath) == saved && Property<Shortcut>(keyboard, "CaptureShortcut").Text == "Ctrl+Alt+S"
                    && !Property<bool>(Field(context, "_settings")!, "ShowNotifications"),
                    "a rejected shortcut conflict preserves persisted and running preferences");
                Check(ApplySettings(context, Settings(true, "D")) is { Length: > 0 }
                    && File.ReadAllText(settingsPath) == saved,
                    "invalid bare letter capture shortcuts cannot replace the current configuration");

                Arm();
                var blocked = Path.Combine(Path.GetDirectoryName(data)!, "not-a-directory");
                File.WriteAllText(blocked, "configuration write failure fixture");
                Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", blocked);
                try
                {
                    Check(ApplySettings(context, Settings(true, "F12")) is { Length: > 0 }, "settings write failures are reported to the caller");
                    Check(Property<Shortcut>(keyboard, "CaptureShortcut").Text == "Ctrl+Alt+S"
                        && !Property<bool>(Field(context, "_settings")!, "ShowNotifications") && gate.Phase == CapturePhase.Armed,
                        "failed settings writes preserve the current shortcut, visibility and armed screenshot");
                }
                finally { Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", data); }
                Check(File.ReadAllText(settingsPath) == saved, "failed runtime settings changes leave the last saved configuration intact");
                var committed = AttachDelivery(context, sequence, "preparing");
                gate.Cancel();
                Check(ApplySettings(context, Settings(false, "Ctrl+Shift+S")) == null
                    && ReferenceEquals(Field(context, "_delivery"), committed),
                    "changing the capture shortcut preserves an already committed delivery");
                Invoke(context, "CancelDelivery", false);
            }
            using var restarted = (ApplicationContext)Create("TrayApplication", (object?)null);
            Check(!Property<bool>(Field(restarted, "_settings")!, "ShowNotifications")
                && Property<Shortcut>(Field(restarted, "_keyboard")!, "CaptureShortcut").Text == "Ctrl+Shift+S",
                "restarting the tray restores hidden notifications and the customized capture shortcut");
        });
    }
}
