using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using QuickShot.Core;

internal static class Program
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly App = Assembly.Load("QuickShot");
    private static readonly List<string> Checks = [];
    private static object Create(string name, params object?[] args) => Activator.CreateInstance(App.GetType("QuickShot.App." + name)!, Instance, null, args, null)!;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Instance)!.GetValue(value);
    private static object? Call(object value, string name, params object?[] args) => value.GetType().GetMethod(name, Instance)!.Invoke(value, args);
    private static bool Enabled(object value) => (bool)value.GetType().GetProperty("Enabled", Instance)!.GetValue(value)!;
    private static void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Checks.Add(name); }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static object Settings(bool visible)
    {
        var result = Create("AppSettings"); result.GetType().GetProperty("ShowNotifications")!.SetValue(result, visible); return result;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !args.Contains("--background")) return 2;
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("QUICKSHOT_INSTANCE_ID", "ui-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("QUICKSHOT_DATA_DIR", Path.Combine(output, "data"));
        Environment.SetEnvironmentVariable("QUICKSHOT_NO_REGISTER", "1");
        ApplicationConfiguration.Initialize();
        try
        {
            CheckDashboard(output); CheckHint(output); CheckNotice(output); CheckFullscreenOverlays(); CheckWindowVisibility(); CheckActivity(); CheckBrowserBootstrap();
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("UI checks passed: " + Checks.Count); return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = false, checks = Checks, error = error.ToString() }));
            Console.Error.WriteLine(error); return 1;
        }
    }

    private static void CheckDashboard(string output)
    {
        using var dashboard = (Form)Create("DashboardForm", true); dashboard.Opacity = 0;
        dashboard.Show(); Application.DoEvents();
        var controls = Descendants(dashboard).ToArray();
        Check(dashboard.Text == "QuickShot", "QuickShot program branding is displayed");
        Check(!controls.Any(control => control.Text.Contains("FastStone") || control.Text.Contains("截图快捷键")), "dashboard contains no dedicated capture-tool controls");
        Check(!controls.OfType<Button>().Any(button => button.Text == "使用说明"), "dashboard has no instructions button");
        Check(App.GetType("QuickShot.App.InstallationForm") == null && App.GetType("QuickShot.App.CaptureShortcutBox") == null, "legacy installer and capture settings types are removed");
        Check(!App.GetManifestResourceNames().Contains("QuickShot.Help.html"), "help is not embedded in the executable");
        var notifications = (CheckBox)Field(dashboard, "_showNotifications")!;
        Check(notifications.Checked, "notification preference is enabled by default");
        var property = dashboard.GetType().GetProperty("SettingsRequested", Instance)!;
        var parameter = Expression.Parameter(property.PropertyType.GenericTypeArguments[0]);
        var count = 0; var fail = false; object? received = null;
        Func<object, string?> handler = settings => { received = settings; count++; return fail ? "无法保存测试设置" : null; };
        property.SetValue(dashboard, Expression.Lambda(property.PropertyType, Expression.Invoke(Expression.Constant(handler), Expression.Convert(parameter, typeof(object))), parameter).Compile());
        Call(dashboard, "RefreshSettings", Settings(false));
        Check(!notifications.Checked && count == 0, "refreshing settings does not save recursively");
        notifications.Checked = true;
        Check(count == 1 && (bool)received!.GetType().GetProperty("ShowNotifications")!.GetValue(received)!, "checkbox saves its preference immediately");
        fail = true; notifications.Checked = false;
        Check(notifications.Checked && ((Label)Field(dashboard, "_status")!).Text == "无法保存测试设置", "save failures restore the checkbox and show the error");
        Call(dashboard, "RefreshSettings", Settings(false));
        Call(dashboard, "RefreshState", true, new[] { new TargetBinding("ui", "Ctrl+Alt+C", "豆包", "https://www.doubao.com/chat/") }, "右下角提示已隐藏。投递结果仍会在此处显示。");
        Check(!notifications.Checked && ((Label)Field(dashboard, "_status")!).Text.Contains("投递结果"), "hidden notifications retain dashboard status");
        Save(dashboard, Path.Combine(output, "dashboard.png")); CheckBounds(dashboard);
        dashboard.Size = dashboard.MinimumSize; Application.DoEvents();
        Save(dashboard, Path.Combine(output, "dashboard-minimum.png")); CheckBounds(dashboard);
        dashboard.Close();
        Check(dashboard.IsDisposed, "closing the dashboard releases its window resources instead of retaining a hidden window");
    }

    private static void CheckBounds(Form form)
    {
        foreach (var control in Descendants(form).Where(control => control.Visible && control is Label or Button or CheckBox or ListView))
        {
            var origin = form.PointToClient(control.PointToScreen(Point.Empty));
            Check(form.ClientRectangle.Contains(new Rectangle(origin, control.Size)), "dashboard control remains in client area: " + control.GetType().Name);
        }
    }

    private static void CheckHint(string output)
    {
        foreach (var count in new[] { 1, 4, 26 })
        {
            using var form = (Form)Create("HintForm"); form.Opacity = 0;
            var bindings = Enumerable.Range(0, count).Select(index => new TargetBinding("ui-" + index, "Ctrl+Shift+" + (char)('A' + index), "接收目标 " + (index + 1), "https://www.doubao.com/chat/")).ToArray();
            var foreground = GetForegroundWindow(); Call(form, "UpdateHint", bindings, 10); Application.DoEvents();
            Check(Screen.FromHandle(foreground).WorkingArea.Contains(form.Bounds), $"hint {count}: stays in the foreground screen's working area");
            Check(form.Width <= 320 * form.DeviceDpi / 96f * (int)Field(form, "_columns")! + 1, $"hint {count}: compact width");
            Check((int)Field(form, "_keyWidth")! + 2 * (int)Field(form, "_padding")! < form.Width / (int)Field(form, "_columns")!, $"hint {count}: full shortcut fits");
            Check(GetForegroundWindow() == foreground, $"hint {count}: does not steal focus");
            Save(form, Path.Combine(output, "hint-" + count + ".png")); form.Hide();
        }
    }

    private static void CheckNotice(string output)
    {
        using var notice = (Form)Create("StatusNoticeForm"); notice.Opacity = 0;
        var foreground = GetForegroundWindow(); Call(notice, "ShowMessage", "目标窗口未就绪，本次未粘贴。请检查目标网页后重新复制图片。");
        var dismiss = Field(notice, "_dismiss")!;
        Check(notice.Visible && Enabled(dismiss), "status notice has an active dismissal timer");
        Check(GetForegroundWindow() == foreground, "status notice does not steal focus");
        Check(Screen.FromPoint(Cursor.Position).WorkingArea.Contains(notice.Bounds), "status notice stays in working area");
        Save(notice, Path.Combine(output, "notice.png")); Call(dismiss, "OnTick", EventArgs.Empty);
        Check(!notice.Visible && !Enabled(dismiss), "dismissed status notice stops its timer");
    }

    private static void CheckActivity()
    {
        using var context = (ApplicationContext)Create("TrayApplication", (object?)null);
        var gate = (CaptureGate)Field(context, "_gate")!;
        var timer = Field(context, "_timer")!; var keyboard = Field(context, "_keyboard")!; var mouse = Field(context, "_mouse")!;
        Check(!Enabled(timer) && !Enabled(keyboard) && !Enabled(mouse), "idle state has no polling or input hooks");
        gate.Initialize(1); gate.ClipboardChanged(2, true, DateTimeOffset.UtcNow); Call(context, "UpdateActivityTimer");
        Check(Enabled(timer) && Enabled(keyboard) && Enabled(mouse), "armed state enables only the required monitors");
        Call(context, "CancelForOtherInput");
        Check(!Enabled(timer) && !Enabled(keyboard) && !Enabled(mouse), "cancellation returns to no polling or input hooks");
    }

    private static void CheckFullscreenOverlays()
    {
        foreach (var name in new[] { "HintForm", "StatusNoticeForm" })
        {
            using var notification = (Form)Create(name);
            if (name == "HintForm") Call(notification, "UpdateHint", new[] { new TargetBinding("overlay", "C", "测试目标", "https://example.com/") }, 10);
            else Call(notification, "ShowMessage", "测试结果");
            using var cover = new Form { FormBorderStyle = FormBorderStyle.None, Bounds = Screen.PrimaryScreen!.Bounds, TopMost = true, Text = "QuickShot owned fullscreen overlay fixture" };
            cover.Show(); cover.Activate(); Application.DoEvents();
            var foreground = GetForegroundWindow();
            var layer = (System.Windows.Forms.Timer)App.GetType("QuickShot.App.NotificationForm")!.GetField("_layer", Instance)!.GetValue(notification)!;
            Check(layer.Enabled, name + ": visible notification maintains its overlay layer");
            Call(layer, "OnTick", EventArgs.Empty); Application.DoEvents();
            var windows = new List<nint>();
            EnumWindows((window, _) => { windows.Add(window); return true; }, 0);
            Check(windows.IndexOf(notification.Handle) < windows.IndexOf(cover.Handle), name + ": notification is above a later topmost fullscreen window");
            Check(GetForegroundWindow() == foreground, name + ": raising the overlay preserves fullscreen keyboard focus");
            notification.Hide();
            Check(!layer.Enabled, name + ": hidden notification stops layer maintenance");
        }
    }

    private static void CheckWindowVisibility()
    {
        var visible = App.GetType("QuickShot.App.ForegroundVisibility")!.GetMethod("IsUnobscured", BindingFlags.Static | BindingFlags.NonPublic)!;
        var area = Screen.PrimaryScreen!.WorkingArea;
        using var target = new Form { Bounds = new Rectangle(area.Left + 50, area.Top + 50, 300, 250), StartPosition = FormStartPosition.Manual, TopMost = true };
        target.Show(); Application.DoEvents();
        using var owned = new Form { Bounds = target.Bounds, FormBorderStyle = FormBorderStyle.None, TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual };
        owned.Owner = target; owned.Show(); owned.Activate(); Application.DoEvents();
        Check((bool)visible.Invoke(null, [target.Handle])!, "owned browser-style native popups do not count as an external fullscreen cover");
        using var other = new Form { Bounds = target.Bounds, FormBorderStyle = FormBorderStyle.None, TopMost = true, StartPosition = FormStartPosition.Manual };
        other.Show(); other.Activate(); Application.DoEvents();
        Check(!(bool)visible.Invoke(null, [target.Handle])!, "an unrelated topmost window is detected as a cover");
    }

    private static void CheckBrowserBootstrap()
    {
        var create = App.GetType("QuickShot.App.DesktopUi")!.GetMethod("CreateEdgeStartInfo", BindingFlags.Static | BindingFlags.NonPublic)!;
        const string connect = "chrome-extension://kgjahiekhopambbcmenggajjghlmbhel/connect.html";
        var info = (System.Diagnostics.ProcessStartInfo)create.Invoke(null, [connect])!;
        Check(!info.UseShellExecute && info.ArgumentList.SequenceEqual(new[] { connect }), "automatic connection retains the validated single receiving page path");
    }

    private static void Save(Form form, string file)
    {
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    private delegate bool Enumerator(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Enumerator callback, nint parameter);
}
