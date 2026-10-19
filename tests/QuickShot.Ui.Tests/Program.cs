using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Linq.Expressions;
using QuickShot.Core;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly App = Assembly.Load("QuickShot");
    private static readonly List<string> Checks = [];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !args.Contains("--background"))
        {
            Console.Error.WriteLine("Usage: QuickShot.Ui.Tests <dedicated-output-directory> --background");
            return 2;
        }
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("QUICKSHOT_INSTANCE_ID", "ui-test-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", Path.Combine(output, "data"));
        Environment.SetEnvironmentVariable("QUICKSHOT_NO_REGISTER", "1");
        ApplicationConfiguration.Initialize();
        try
        {
            CheckHintLayouts(output);
            CheckNotice(output);
            CheckActivityStates();
            CheckDashboardSettings(output);
            CheckExternalHelp();
            CheckBrowserBootstrap();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"UI lifecycle checks passed: {Checks.Count}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = false, checks = Checks, error = exception.ToString() }));
            return 1;
        }
    }

    private static void CheckExternalHelp()
    {
        var help = Path.Combine(AppContext.BaseDirectory, "使用说明.html");
        Check(File.ReadAllText(help).Contains("QUICKSHOT 1.3.6", StringComparison.Ordinal), "help: current standalone instructions are included");
        Check(!App.GetManifestResourceNames().Contains("QuickShot.Help.html"), "help: no instructions are embedded in the executable");
        var temporary = help + "." + Guid.NewGuid().ToString("N") + ".test-backup";
        File.Move(help, temporary);
        try
        {
            using var context = (ApplicationContext)Create("TrayApplication", (object?)null);
            using var dashboard = (Form)Create("DashboardForm", true);
            Check(!File.Exists(help) && Field(context, "_keyboard") != null, "help: tray and dashboard start normally after instructions are removed");
            IEnumerable<Control> Descendants(Control parent)
            {
                foreach (Control child in parent.Controls)
                {
                    yield return child;
                    foreach (var descendant in Descendants(child)) yield return descendant;
                }
            }
            Check(!Descendants(dashboard).OfType<Button>().Any(button => button.Text == "使用说明"), "dashboard: no instructions button");
        }
        finally { File.Move(temporary, help); }
    }

    private static object Create(string name, params object?[] parameters) => Activator.CreateInstance(App.GetType("QuickShot.App." + name)!, Instance, null, parameters, null)!;

    private static void CheckBrowserBootstrap()
    {
        var create = App.GetType("QuickShot.App.DesktopUi")!.GetMethod("CreateEdgeStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!;
        const string connect = "chrome-extension://kgjahiekhopambbcmenggajjghlmbhel/connect.html";
        var bootstrap = (System.Diagnostics.ProcessStartInfo)create.Invoke(null, [connect])!;
        Check(!bootstrap.UseShellExecute && bootstrap.WindowStyle == System.Diagnostics.ProcessWindowStyle.Normal,
            "automatic connection: retains the version 1.3.4 normal direct browser launch");
        Check(bootstrap.ArgumentList.SequenceEqual(new[] { connect }),
            "automatic connection: opens only its connection URL without special window flags");
        var manager = (System.Diagnostics.ProcessStartInfo)create.Invoke(null, ["chrome-extension://kgjahiekhopambbcmenggajjghlmbhel/options.html"])!;
        Check(!manager.UseShellExecute && manager.WindowStyle == System.Diagnostics.ProcessWindowStyle.Normal
            && manager.ArgumentList.SequenceEqual(new[] { "chrome-extension://kgjahiekhopambbcmenggajjghlmbhel/options.html" }),
            "manual management: opens normally with only the management URL");
    }
    private static object? Field(object owner, string name) => owner.GetType().GetField(name, Instance)!.GetValue(owner);
    private static void Invoke(object owner, string name, params object?[] parameters) => owner.GetType().GetMethod(name, Instance)!.Invoke(owner, parameters);
    private static bool MouseEnabled(object mouse) => (bool)mouse.GetType().GetProperty("Enabled", Instance)!.GetValue(mouse)!;
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Checks.Add(name); }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static object Settings(bool showNotifications, string captureShortcut)
    {
        var settings = Create("AppSettings");
        settings.GetType().GetProperty("ShowNotifications")!.SetValue(settings, showNotifications);
        settings.GetType().GetProperty("CaptureShortcut")!.SetValue(settings, captureShortcut);
        return settings;
    }

    private static T Property<T>(object owner, string name) => (T)owner.GetType().GetProperty(name, Instance)!.GetValue(owner)!;

    private static void SetSettingsHandler(object dashboard, Func<object, string?> handler)
    {
        var property = dashboard.GetType().GetProperty("SettingsRequested", Instance)!;
        var parameter = Expression.Parameter(property.PropertyType.GenericTypeArguments[0], "settings");
        var body = Expression.Invoke(Expression.Constant(handler), Expression.Convert(parameter, typeof(object)));
        property.SetValue(dashboard, Expression.Lambda(property.PropertyType, body, parameter).Compile());
    }

    private static void CheckDashboardSettings(string output)
    {
        using var dashboard = (Form)Create("DashboardForm", true);
        dashboard.Opacity = 0;
        dashboard.Show(); Application.DoEvents();
        var notifications = (CheckBox)Field(dashboard, "_showNotifications")!;
        var capture = (TextBox)Field(dashboard, "_captureShortcut")!;
        var instructions = (Label)Field(dashboard, "_instructions")!;
        var status = (Label)Field(dashboard, "_status")!;
        var save = Descendants(dashboard).OfType<Button>().Single(button => button.Text == "保存快捷键");
        Check(notifications.Checked && notifications.Text.Contains("显示右下角提示", StringComparison.Ordinal),
            "dashboard settings: notification toggle defaults on with a visible label");
        Check(capture.ReadOnly && !capture.ShortcutsEnabled && capture.Text == "Alt+Shift+D"
            && capture.AccessibleName == "截图快捷键", "dashboard settings: screenshot shortcut uses a named read-only recorder");
        Check(instructions.Text.Contains("Alt+Shift+D", StringComparison.Ordinal),
            "dashboard settings: instructions reflect the active screenshot shortcut");
        Check(!Descendants(dashboard).OfType<Button>().Any(button => button.Text == "使用说明"),
            "dashboard settings: adds settings without an instructions button");

        var requested = new List<(bool ShowNotifications, string CaptureShortcut)>();
        string? failure = null;
        SetSettingsHandler(dashboard, settings =>
        {
            requested.Add((Property<bool>(settings, "ShowNotifications"), Property<string>(settings, "CaptureShortcut")));
            return failure;
        });
        var configured = Settings(false, "Ctrl+Shift+F6");
        Invoke(dashboard, "RefreshSettings", configured);
        Check(!notifications.Checked && capture.Text == "Ctrl+Shift+F6" && requested.Count == 0,
            "dashboard settings: loading saved configuration does not request another save");
        Check(instructions.Text.Contains("Ctrl+Shift+F6", StringComparison.Ordinal),
            "dashboard settings: saved screenshot shortcut replaces the default in instructions");
        notifications.Checked = true;
        Check(requested.Count == 1 && requested[^1] == (true, "Ctrl+Shift+F6"),
            "dashboard settings: toggling notifications requests immediate persistence without changing the shortcut");

        Invoke(capture, "RecordKey", Keys.PrintScreen, false);
        Check(capture.Text == "PrintScreen" && requested.Count == 1,
            "dashboard settings: PrintScreen records a draft without saving or triggering a screenshot");
        Invoke(dashboard, "RefreshState", true, Array.Empty<TargetBinding>(), "网页状态已刷新");
        Invoke(dashboard, "RefreshSettings", Settings(true, "Ctrl+Shift+F6"));
        Check(capture.Text == "PrintScreen" && instructions.Text.Contains("Ctrl+Shift+F6", StringComparison.Ordinal),
            "dashboard settings: status and unchanged configuration refresh preserve an unsaved shortcut draft");
        save.PerformClick();
        Check(requested.Count == 2 && requested[^1] == (true, "PrintScreen")
            && instructions.Text.Contains("PrintScreen", StringComparison.Ordinal),
            "dashboard settings: saving applies the recorded shortcut and refreshes instructions");

        Invoke(capture, "RecordKey", Keys.Control | Keys.Shift | Keys.F6, false);
        Check(capture.Text == "Ctrl+Shift+F6", "dashboard settings: a three-key function shortcut is recorded");
        Invoke(capture, "RecordKey", Keys.Control | Keys.Alt | Keys.Shift | Keys.D, false);
        Check(capture.Text == "Ctrl+Shift+F6" && status.Text.Length > 0 && requested.Count == 2,
            "dashboard settings: a four-key combination is rejected with feedback and keeps the previous draft");
        Invoke(capture, "RecordKey", Keys.C, false);
        Check(capture.Text == "Ctrl+Shift+F6" && status.Text.Contains("修饰键", StringComparison.Ordinal),
            "dashboard settings: an unmodified letter is rejected with guidance");
        Invoke(capture, "RecordKey", Keys.ControlKey, false);
        Check(capture.Text == "Ctrl+Shift+F6", "dashboard settings: a modifier by itself leaves the draft unchanged");
        Invoke(capture, "RecordKey", Keys.F6, false);
        Check(capture.Text == "F6", "dashboard settings: an unmodified F1-F12 key is supported");
        Invoke(capture, "RecordKey", Keys.C, true);
        Check(capture.Text == "Win+C", "dashboard settings: recorder accepts a Windows-key combination");

        failure = "保存设置失败：目录不可写。";
        save.PerformClick();
        Check(requested.Count == 3 && requested[^1] == (true, "Win+C") && capture.Text == "PrintScreen"
            && instructions.Text.Contains("PrintScreen", StringComparison.Ordinal) && status.Text == failure,
            "dashboard settings: failed shortcut save restores the active value and reports its failure");
        notifications.Checked = false;
        Check(requested.Count == 4 && requested[^1] == (false, "PrintScreen") && notifications.Checked
            && capture.Text == "PrintScreen" && status.Text == failure,
            "dashboard settings: failed notification save restores the checkbox and retains the active shortcut");

        failure = null;
        notifications.Checked = false;
        Invoke(capture, "RecordKey", Keys.Control | Keys.Shift | Keys.F6, false);
        save.PerformClick();
        Invoke(dashboard, "RefreshState", false,
            new[] { new TargetBinding("ui-settings", "Ctrl+Alt+C", "豆包", "https://www.doubao.com/chat/") },
            "右下角提示已关闭。正在等待 FastStone 截图；投递结果仍在此处显示。");
        Check(!notifications.Checked && capture.Text == "Ctrl+Shift+F6" && status.Text.Contains("提示已关闭", StringComparison.Ordinal),
            "dashboard settings: hidden notices retain a visible status and the configured screenshot shortcut");
        Application.DoEvents();
        CheckDashboardBounds(dashboard, "default size");
        Save(dashboard, Path.Combine(output, "dashboard-settings.png"));
        dashboard.Size = dashboard.MinimumSize;
        Application.DoEvents();
        CheckDashboardBounds(dashboard, "minimum size");
        Save(dashboard, Path.Combine(output, "dashboard-settings-minimum.png"));
        dashboard.Hide();
    }

    private static void CheckDashboardBounds(Form dashboard, string size)
    {
        var visibleControls = Descendants(dashboard).Where(control => control.Visible).ToArray();
        Check(visibleControls.All(control => dashboard.ClientRectangle.Contains(
            new Rectangle(dashboard.PointToClient(control.PointToScreen(Point.Empty)), control.Size))),
            $"dashboard settings {size}: all visible controls fit in the client area");
        var status = (Label)Field(dashboard, "_status")!;
        var instructions = (Label)Field(dashboard, "_instructions")!;
        var statusTextHeight = TextRenderer.MeasureText(status.Text, status.Font, new Size(status.ClientSize.Width, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        Check(statusTextHeight <= status.ClientSize.Height && instructions.PreferredSize.Height <= instructions.Height,
            $"dashboard settings {size}: instructions and the displayed status text fit their labels");
    }

    private static void CheckHintLayouts(string output)
    {
        foreach (var count in new[] { 1, 4, 26 })
        {
            using var form = (Form)Create("HintForm");
            form.Opacity = 0;
            var bindings = Enumerable.Range(0, count).Select(index => new TargetBinding("ui-" + index, "Ctrl+Shift+" + (char)('A' + index), "接收目标 " + (index + 1), "https://www.doubao.com/chat/")).ToArray();
            var foreground = GetForegroundWindow();
            Invoke(form, "UpdateHint", bindings, 10);
            Application.DoEvents();
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            var scale = form.DeviceDpi / 96f;
            Check(area.Contains(form.Bounds), $"hint {count}: remains in working area");
            Check(form.Width <= 320 * scale * (int)Field(form, "_columns")! + 1, $"hint {count}: compact width");
            Check((int)Field(form, "_keyWidth")! + 2 * (int)Field(form, "_padding")! < form.Width / (int)Field(form, "_columns")!, $"hint {count}: complete shortcut fits");
            Check(GetForegroundWindow() == foreground, $"hint {count}: no focus taken");
            if (count == 1) Check(form.Height <= 112 * scale, "single hint: compact height");
            Save(form, Path.Combine(output, $"hint-{count}.png"));
            form.Hide();
        }
    }

    private static void CheckNotice(string output)
    {
        using var notice = (Form)Create("StatusNoticeForm");
        notice.Opacity = 0;
        var foreground = GetForegroundWindow();
        Invoke(notice, "ShowMessage", "未能激活目标 Edge 窗口，本次未粘贴。请确认浏览器没有管理员权限或模态对话框。");
        Application.DoEvents();
        var dismiss = (System.Windows.Forms.Timer)Field(notice, "_dismiss")!;
        Check(notice.Visible && dismiss.Enabled, "status notice: visible with active auto-dismiss");
        Check(notice.Width <= 320 * notice.DeviceDpi / 96f + 1, "status notice: compact width");
        Check(Screen.FromPoint(Cursor.Position).WorkingArea.Contains(notice.Bounds), "status notice: remains in working area");
        Check(GetForegroundWindow() == foreground, "status notice: no focus taken");
        Save(notice, Path.Combine(output, "status-notice.png"));
        Invoke(dismiss, "OnTick", EventArgs.Empty);
        Check(!notice.Visible && !dismiss.Enabled, "expired status notice: hidden and timer stopped");
    }

    private static void CheckActivityStates()
    {
        using var context = (ApplicationContext)Create("TrayApplication", (object?)null);
        var timer = (System.Windows.Forms.Timer)Field(context, "_timer")!;
        var gate = (CaptureGate)Field(context, "_gate")!;
        var mouse = Field(context, "_mouse")!;
        Check(!timer.Enabled && !MouseEnabled(mouse), "idle: no polling timer or mouse hook");
        gate.CaptureStarted(1, DateTimeOffset.UtcNow);
        Invoke(context, "UpdateActivityTimer");
        Check(timer.Enabled && timer.Interval == 1000 && !MouseEnabled(mouse), "capture selection: slow expiry timer and no mouse hook");
        gate.ClipboardChanged(2, true, true, DateTimeOffset.UtcNow);
        Invoke(context, "UpdateActivityTimer");
        Check(timer.Enabled && timer.Interval == 250 && MouseEnabled(mouse), "armed: countdown timer and mouse cancellation hook");
        Invoke(context, "CancelForOtherInput");
        Check(gate.Phase == CapturePhase.Idle && !timer.Enabled && !MouseEnabled(mouse), "cancel: timer and mouse hook stopped");
        var deliveryType = context.GetType().GetNestedType("Delivery", BindingFlags.NonPublic)!;
        var delivery = Activator.CreateInstance(deliveryType, Instance, null, [new TargetBinding(Guid.NewGuid().ToString(), "C", "测试", "https://example.com/"), 0u], null);
        context.GetType().GetField("_delivery", Instance)!.SetValue(context, delivery);
        Invoke(context, "UpdateActivityTimer");
        Check(timer.Enabled && timer.Interval == 100 && !MouseEnabled(mouse), "delivery: short focus polling without mouse cancellation hook");
        Invoke(context, "CancelDelivery", false);
        Check(!timer.Enabled && !MouseEnabled(mouse), "delivery cancelled: return to no polling or mouse hook");
        var native = App.GetType("QuickShot.App.NativeMethods")!;
        var sequence = (uint)native.GetMethod("GetClipboardSequenceNumber", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var blocked = Activator.CreateInstance(deliveryType, Instance, null,
            [new TargetBinding(Guid.NewGuid().ToString(), "C", "不存在的测试目标", "https://example.com/"), sequence], null)!;
        deliveryType.GetField("Phase", Instance)!.SetValue(blocked, "waitingForActivation");
        deliveryType.GetField("ActivationDeadline", Instance)!.SetValue(blocked, DateTimeOffset.UtcNow.AddSeconds(-1));
        context.GetType().GetField("_delivery", Instance)!.SetValue(context, blocked);
        Invoke(context, "Tick");
        Check((string)deliveryType.GetField("Phase", Instance)!.GetValue(blocked)! == "verifying", "native activation denied: request browser verification without dispatching paste");
        Invoke(context, "CancelDelivery", false);
        Check(!timer.Enabled && !MouseEnabled(mouse), "activation fallback cancelled: return to idle");
    }

    private static void Save(Form form, string file)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
