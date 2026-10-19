using System.Text.Json;
using System.Text.Json.Nodes;
using QuickShot.Core;
using Shortcut = QuickShot.Core.Shortcut;

namespace QuickShot.App;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly Control _dispatcher = new();
    private readonly CaptureGate _gate = new();
    private readonly KeyboardMonitor _keyboard;
    private readonly MouseMonitor _mouse;
    private readonly ClipboardWindow _clipboard = new();
    private readonly PipeServer _server = new();
    private readonly NotifyIcon _tray;
    private readonly Icon _icon = DesktopUi.CreateIcon();
    private readonly HintForm _hint = new();
    private readonly StatusNoticeForm _notice = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private AppSettings _settings = AppSettings.Load();
    private DashboardForm? _dashboard;
    private List<TargetBinding> _bindings = [];
    private Dictionary<string, Shortcut> _shortcuts = [];
    private Shortcut[] _shortcutList = [];
    private static readonly JsonSerializerOptions BindingJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private PipePeer? _peer;
    private bool _peerReady;
    private string? _profileId;
    private Delivery? _delivery;
    private string _status = "首次使用：运行 QuickShot.exe，在 Edge 加载 extension 文件夹，然后绑定聊天输入框。";
    private bool _paused, _disposed;
    private string CacheFile => Path.Combine(LocalLog.DataDirectory, "targets.json");

    private sealed class Delivery(TargetBinding target, uint sequence)
    {
        internal readonly TargetBinding Target = target;
        internal readonly uint Sequence = sequence;
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal string Token => $"[QuickShot:{Id}]";
        internal DateTimeOffset Deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        internal string Phase = "connecting";
        internal DateTimeOffset ActivationDeadline;
        internal readonly CancellationTokenSource FocusCancellation = new();
        internal Task<bool>? FocusTask;
    }

    internal TrayApplication(string? registrationError = null)
    {
        _status = registrationError ?? "打开 Edge 快投扩展可连接本程序。便携模式也支持直接运行，无需复制到固定目录。";
        _ = _dispatcher.Handle;
        LoadCache();
        _keyboard = new KeyboardMonitor { CaptureShortcut = AppSettings.ParseCaptureShortcut(_settings.CaptureShortcut) };
        _keyboard.CaptureStarted = () =>
        {
            if (_paused || _delivery != null || _dashboard?.RecordingCaptureShortcut == true) return;
            _gate.CaptureStarted(NativeMethods.GetClipboardSequenceNumber(), DateTimeOffset.UtcNow);
            _hint.Hide(); _notice.Hide(); UpdateActivityTimer();
        };
        _keyboard.KeyInput = ConsumeKey;
        _mouse = new MouseMonitor { Clicked = () => { if (_gate.Phase == CapturePhase.Armed) CancelForOtherInput(); } };
        _clipboard.Changed += ClipboardChanged;
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开快投", null, (_, _) => ShowDashboard());
        menu.Items.Add("管理接收目标", null, (_, _) => DashboardForm.OpenManager());
        var pause = new ToolStripMenuItem("暂停快捷投递") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => { _paused = pause.Checked; _gate.Cancel(); _hint.Hide(); CancelDelivery(false); };
        menu.Items.Add(pause); menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("移除快投连接并退出", null, (_, _) => RemoveConnection());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Text = "快投 QuickShot · FastStone → Edge", Icon = _icon, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowDashboard();
        _server.Message += (peer, message) => Post(() => Receive(peer, message));
        _server.Disconnected += peer => Post(() =>
        {
            if (_peer?.Id != peer.Id) return;
            _peer = null;
            _peerReady = false;
            if (_delivery != null) Fail("Edge 连接已断开，图片仍在剪贴板。请重新截图后重试。");
            RefreshDashboard();
        });
        _server.Start();
        _timer.Tick += (_, _) => Tick();
        if (!Environment.GetCommandLineArgs().Contains("--background")) ShowDashboard();
    }

    private void Post(Action action)
    {
        if (_disposed || _dispatcher.IsDisposed) return;
        try { _dispatcher.BeginInvoke(() => { if (!_disposed) action(); }); }
        catch (InvalidOperationException) { }
    }

    private void CancelForOtherInput()
    {
        if (_delivery != null) return;
        _gate.Cancel(); _hint.Hide();
        UpdateActivityTimer();
    }

    private bool ConsumeKey(KeyStroke stroke)
    {
        if (_delivery != null) return false;
        if (stroke.IsDown && stroke.VirtualKey == NativeMethods.VkEscape) { CancelForOtherInput(); return false; }
        if (_paused) return false;
        _gate.Expire(DateTimeOffset.UtcNow);
        if (_gate.Phase != CapturePhase.Armed) { UpdateActivityTimer(); return false; }
        var decision = ShortcutMatcher.Evaluate(_shortcutList, stroke);
        if (decision == ShortcutDecision.Cancel) { CancelForOtherInput(); return false; }
        if (decision != ShortcutDecision.Match) return false;
        var target = _bindings.FirstOrDefault(t => _shortcuts.TryGetValue(t.Key, out var shortcut) && shortcut.Matches(stroke.VirtualKey, stroke.Modifiers));
        if (target == null || !_gate.TryConsume(NativeMethods.GetClipboardSequenceNumber(), DateTimeOffset.UtcNow)) { UpdateActivityTimer(); return false; }
        var sequence = _gate.ImageSequence;
        _hint.Hide();
        var delivery = new Delivery(target, sequence); _delivery = delivery; UpdateActivityTimer();
        Post(() => StartDelivery(delivery));
        return true;
    }

    private void ClipboardChanged()
    {
        if (_paused || (_gate.Phase == CapturePhase.Idle && _delivery == null)) return;
        var now = DateTimeOffset.UtcNow;
        _gate.Expire(now);
        var sequence = NativeMethods.GetClipboardSequenceNumber();
        if (_delivery != null && _delivery.Phase != "pasting" && sequence != _delivery.Sequence)
            Fail("剪贴板已变化，本次投递停止。请重新截图。");
        var containsImage = _gate.Phase == CapturePhase.WaitingForImage && sequence != _gate.ImageSequence && NativeMethods.ClipboardHasImage;
        var fromFastStone = containsImage ? NativeMethods.ClipboardFromFastStone() : null;
        if (_gate.ClipboardChanged(sequence, containsImage, fromFastStone, now))
        {
            if (_bindings.Count == 0) { _gate.Cancel(); Notify("请先在 Edge 扩展中绑定一个接收目标。"); }
            else { _keyboard.MarkScreenshotReady(); _notice.Hide(); RefreshHint(); }
        }
        if (_gate.Phase != CapturePhase.Armed) _hint.Hide();
        UpdateActivityTimer();
    }

    private void StartDelivery(Delivery delivery)
    {
        if (_delivery != delivery) return;
        if (NativeMethods.GetClipboardSequenceNumber() != delivery.Sequence || !NativeMethods.ClipboardHasImage) { Fail("剪贴板已变化，请重新截图。"); return; }
        _status = $"正在准备 {delivery.Target.Name}…"; RefreshDashboard();
        if (_peer != null && _peerReady) Prepare();
        else
        {
            try { DesktopUi.OpenEdge($"chrome-extension://{Protocol.ExtensionId}/connect.html"); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Fail("无法启动 Edge：" + e.Message); }
        }
    }

    private void Prepare()
    {
        if (_delivery == null || _peer == null || !_peerReady || _delivery.Phase != "connecting") return;
        if (!_bindings.Any(t => t.Id == _delivery.Target.Id)) { Fail("原接收目标已被删除，请重新绑定。"); return; }
        _delivery.Phase = "preparing";
        Send(new JsonObject { ["type"] = "prepare", ["requestId"] = _delivery.Id, ["bindingId"] = _delivery.Target.Id, ["titleToken"] = _delivery.Token });
    }

    private void Receive(PipePeer peer, JsonObject message)
    {
        try
        {
            var type = message["type"]?.GetValue<string>();
            if (type == "hello")
            {
                if (message["version"]?.GetValue<int>() != Protocol.Version) { _ = peer.SendAsync(new JsonObject { ["type"] = "rejected", ["message"] = "程序与扩展版本不一致，请重新安装完整发布包。" }); return; }
                var profile = message["profileId"]?.GetValue<string>();
                if (!Guid.TryParse(profile, out _)) return;
                if (_peer != null && _profileId != profile)
                {
                    _ = peer.SendAsync(new JsonObject { ["type"] = "rejected", ["message"] = "已有另一个 Edge 用户配置连接，请先关闭另一个配置中的快投扩展。" }); return;
                }
                if (_peer?.Id != peer.Id) _peer?.Dispose();
                _peer = peer; _profileId = profile;
                _peerReady = false;
                UpdateBindings(message["bindings"] as JsonArray);
                Send(new JsonObject { ["type"] = "helloAck", ["version"] = Protocol.Version, ["captureShortcut"] = _settings.CaptureShortcut });
                _status = "Edge 已连接。截图后按绑定键即可投递。"; RefreshDashboard(); return;
            }
            if (_peer?.Id != peer.Id) return;
            if (type == "ready")
            {
                _peerReady = true;
                // Settings may have changed while the extension was acknowledging hello.
                Send(new JsonObject { ["type"] = "captureShortcut", ["captureShortcut"] = _settings.CaptureShortcut });
                Prepare(); RefreshDashboard(); return;
            }
            if (type == "bindings") { UpdateBindings(message["bindings"] as JsonArray); RefreshDashboard(); return; }
            if (_delivery == null || message["requestId"]?.GetValue<string>() != _delivery.Id) return;
            switch (type)
            {
                case "focusPrepared" when _delivery.Phase == "preparing":
                    _delivery.Phase = "waitingForActivation";
                    _delivery.ActivationDeadline = DateTimeOffset.UtcNow.AddSeconds(4);
                    break;
                case "contentFocusNeeded" when _delivery.Phase == "verifying":
                    _delivery.Phase = "focusingContent";
                    _delivery.ActivationDeadline = DateTimeOffset.UtcNow.AddSeconds(4);
                    StartContentFocus(_delivery);
                    break;
                case "pasteNow" when _delivery.Phase == "verifying":
                    HandlePasteNow();
                    break;
                case "deliveryResult" when _delivery.Phase == "pasting":
                    var observed = message["pasteObserved"]?.GetValue<bool>() == true;
                    _delivery.FocusCancellation.Dispose();
                    _delivery = null;
                    UpdateActivityTimer();
                    Notify(observed ? "已执行粘贴，请在网页确认附件后手动发送。" : "已发出粘贴，但网页未确认接收。请检查附件，避免重复添加。");
                    break;
                case "deliveryError": Fail(message["message"]?.GetValue<string>() ?? "网页未就绪，请检查接收目标。"); break;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException or ArgumentException)
        { LocalLog.Write("message", e); if (_delivery != null) Fail("收到无效的扩展响应，请重新连接。"); }
    }

    private void HandlePasteNow()
    {
        if (_delivery is not { Phase: "verifying" } delivery) return;
        if (!_keyboard.AllDeliveryKeysReleased)
        {
            // A committed delivery survives subsequent typing. Wait out held
            // shortcut/modifier keys rather than risk changing Ctrl+V's chord.
            // The old permission is discarded; Tick requests fresh validation.
            delivery.Phase = "waitingForKeys";
            return;
        }
        if (NativeMethods.GetClipboardSequenceNumber() != delivery.Sequence || !NativeMethods.ClipboardHasImage)
        { Fail("剪贴板发生变化，本次未粘贴。请重新截图。"); return; }
        if (!NativeMethods.IsExpectedEdgeForeground(delivery.Token))
        { Fail("目标 Edge 标签页没有获得焦点，本次未粘贴。"); return; }
        delivery.Phase = "pasting";
        if (!NativeMethods.Paste()) { Fail("Windows 未接受粘贴操作。请确认 Edge 没有以管理员身份运行。"); return; }
        Send(new JsonObject { ["type"] = "pasteDispatched", ["requestId"] = delivery.Id });
    }

    private void UpdateBindings(JsonArray? array, bool persist = true)
    {
        if (array == null || array.Count > 26) throw new FormatException("Invalid bindings.");
        var targets = new List<TargetBinding>();
        var keys = new HashSet<string>(); var ids = new HashSet<string>();
        foreach (var node in array)
        {
            var target = node?.Deserialize<TargetBinding>(BindingJsonOptions) ?? throw new FormatException();
            var shortcut = Shortcut.Parse(target.Key);
            if (!Guid.TryParse(target.Id, out _) || !ids.Add(target.Id) || shortcut.Text != target.Key || !keys.Add(target.Key) || string.IsNullOrWhiteSpace(target.Name) || target.Name.Length > 80 ||
                !Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length != 0)
                throw new FormatException("Invalid target.");
            targets.Add(target);
        }
        var ordered = targets.OrderBy(t => t.Key).ToList();
        if (_bindings.SequenceEqual(ordered)) return;
        _bindings = ordered;
        _shortcuts = _bindings.ToDictionary(target => target.Key, target => Shortcut.Parse(target.Key));
        _shortcutList = _shortcuts.Values.ToArray();
        if (!persist) return;
        try { Directory.CreateDirectory(LocalLog.DataDirectory); File.WriteAllText(CacheFile, JsonSerializer.Serialize(_bindings)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LocalLog.Write("cache", e); }
    }

    private void LoadCache()
    {
        try { if (File.Exists(CacheFile)) UpdateBindings(JsonNode.Parse(File.ReadAllText(CacheFile)) as JsonArray, persist: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException or ArgumentException) { LocalLog.Write("cache", e); }
    }

    private void Tick()
    {
        try
        {
            _gate.Expire(DateTimeOffset.UtcNow);
            RefreshHint();
            if (_delivery == null) return;
            if (_delivery.Phase != "pasting" && NativeMethods.GetClipboardSequenceNumber() != _delivery.Sequence) { Fail("剪贴板已变化，本次投递停止。"); return; }
            if (DateTimeOffset.UtcNow >= _delivery.Deadline) { Fail("等待网页超时。请确认扩展已安装、账号已登录，然后重新截图。"); return; }
            if (_delivery.Phase == "waitingForKeys" && _keyboard.AllDeliveryKeysReleased)
            {
                _delivery.Phase = "verifying";
                Send(new JsonObject { ["type"] = "verifyFocus", ["requestId"] = _delivery.Id });
            }
            else if (_delivery.Phase == "waitingForActivation" && _keyboard.AllDeliveryKeysReleased)
            {
                // Edge's own window API can restore app windows when Windows
                // refuses foreground activation from a background tray process.
                // verifyFocus still checks the exact tab/editor, and pasteNow
                // requires this window's unique marker to be foreground.
                if (NativeMethods.TryActivateEdgeWindow(_delivery.Token) || DateTimeOffset.UtcNow >= _delivery.ActivationDeadline)
                {
                    _delivery.Phase = "verifying";
                    Send(new JsonObject { ["type"] = "verifyFocus", ["requestId"] = _delivery.Id });
                }
            }
            else if (_delivery.Phase == "focusingContent")
            {
                if (DateTimeOffset.UtcNow >= _delivery.ActivationDeadline) { Fail("Edge 网页内容未获得键盘焦点，本次未粘贴。请检查浏览器对话框或重新选择输入框。"); return; }
                if (_delivery.FocusTask?.IsFaulted == true) { LocalLog.Write("focus", _delivery.FocusTask.Exception!); Fail("聚焦网页失败，本次未粘贴。请重新截图。"); return; }
                if (_delivery.FocusTask is { IsCompletedSuccessfully: true } task)
                {
                    if (task.Result)
                    {
                        _delivery.Phase = "verifying";
                        Send(new JsonObject { ["type"] = "verifyFocus", ["requestId"] = _delivery.Id });
                    }
                    else StartContentFocus(_delivery);
                }
            }
        }
        finally { UpdateActivityTimer(); }
    }

    private void UpdateActivityTimer()
    {
        if (_disposed) return;
        if (_gate.Phase != CapturePhase.Armed) _hint.Hide();
        _keyboard.KeyInputEnabled = !_paused && _gate.Phase != CapturePhase.Idle && _delivery == null;
        var monitorMouse = _gate.Phase == CapturePhase.Armed && _delivery == null;
        if (_mouse.Enabled != monitorMouse)
        {
            try { _mouse.Enabled = monitorMouse; }
            catch (System.ComponentModel.Win32Exception e)
            {
                LocalLog.Write("mouse-hook", e);
                if (monitorMouse)
                {
                    _gate.Cancel(); _hint.Hide(); CancelDelivery(false);
                    Notify("无法监听鼠标取消操作，本次投递已停止。请重新启动快投后重试。");
                    return;
                }
            }
        }
        var interval = _delivery != null ? 100 : _gate.Phase == CapturePhase.Armed ? 250 : 1000;
        var active = _delivery != null || _gate.Phase != CapturePhase.Idle;
        if (!active) { _timer.Stop(); return; }
        if (_timer.Interval != interval) _timer.Interval = interval;
        if (!_timer.Enabled) _timer.Start();
    }

    private static void StartContentFocus(Delivery delivery)
    {
        var token = delivery.FocusCancellation.Token;
        delivery.FocusTask = Task.Run(() => BrowserFocus.TryFocusContent(delivery.Token, token));
    }

    private void Send(JsonObject message)
    {
        var peer = _peer; if (peer == null) return;
        _ = SendAsync();
        async Task SendAsync()
        {
            try { await peer.SendAsync(message); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException)
            { LocalLog.Write("send", e); Post(() => { if (_peer?.Id == peer.Id && _delivery != null) Fail("本地连接已断开，请重新连接 Edge。"); }); }
        }
    }

    private void CancelDelivery(bool refresh)
    {
        if (_delivery == null) { UpdateActivityTimer(); return; }
        _delivery.FocusCancellation.Cancel(); _delivery.FocusCancellation.Dispose();
        Send(new JsonObject { ["type"] = "cancel", ["requestId"] = _delivery.Id }); _delivery = null;
        UpdateActivityTimer();
        if (refresh) RefreshDashboard();
    }
    private void Fail(string message)
    {
        if (_delivery is { } delivery)
        {
            var reason = message.ReplaceLineEndings(" ");
            if (reason.Length > 400) reason = reason[..400];
            LocalLog.Event("delivery", $"phase={delivery.Phase}; keysReleased={_keyboard.AllDeliveryKeysReleased}; clipboardUnchanged={NativeMethods.GetClipboardSequenceNumber() == delivery.Sequence}; foregroundMatches={NativeMethods.IsExpectedEdgeForeground(delivery.Token)}; {reason}");
        }
        CancelDelivery(false); Notify(message);
    }
    private void Notify(string message)
    {
        _status = message;
        if (_settings.ShowNotifications) _notice.ShowMessage(message);
        else _notice.Hide();
        RefreshDashboard();
    }
    private void RefreshHint()
    {
        if (_settings.ShowNotifications && _gate.Phase == CapturePhase.Armed)
            _hint.UpdateHint(_bindings, Math.Max(1, (int)Math.Ceiling((_gate.Deadline - DateTimeOffset.UtcNow).TotalSeconds)));
        else _hint.Hide();
    }

    private string? TryApplySettings(AppSettings settings)
    {
        try
        {
            var shortcut = AppSettings.ParseCaptureShortcut(settings.CaptureShortcut);
            var captureChanged = _settings.CaptureShortcut != shortcut.Text;
            if (captureChanged && _bindings.Any(target => target.Key == shortcut.Text))
                return "截图快捷键与已绑定的投递快捷键重复，请使用其他组合。";
            settings = settings with { CaptureShortcut = shortcut.Text };
            settings.Save();
            _settings = settings;
            _keyboard.CaptureShortcut = shortcut;
            if (captureChanged && _delivery == null) _gate.Cancel();
            if (!_settings.ShowNotifications) { _hint.Hide(); _notice.Hide(); }
            if (captureChanged && _peerReady)
                Send(new JsonObject { ["type"] = "captureShortcut", ["captureShortcut"] = shortcut.Text });
            _status = captureChanged
                ? $"截图快捷键已保存为 {shortcut.Text}。请在 FastStone 中设置相同的快捷键。"
                : settings.ShowNotifications ? "已开启右下角提示。" : "已隐藏右下角提示，快捷投递照常运行。结果可在此窗口查看。";
            RefreshHint(); UpdateActivityTimer(); RefreshDashboard();
            return null;
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LocalLog.Write("settings", error);
            return error is FormatException ? error.Message : "无法保存设置，当前设置未更改。请确认便携目录可以写入。";
        }
    }

    private void RefreshDashboard()
    {
        _dashboard?.RefreshSettings(_settings);
        _dashboard?.RefreshState(_peer != null && _peerReady, _bindings, _paused ? "快捷投递已暂停。" : _status);
    }
    private void ShowDashboard()
    {
        if (_dashboard == null)
        {
            _dashboard = new DashboardForm();
            _dashboard.RemoveRequested += RemoveConnection;
            _dashboard.SettingsRequested = TryApplySettings;
        }
        RefreshDashboard(); _dashboard.Show(); _dashboard.Activate();
    }

    private void RemoveConnection()
    {
        if (_disposed) return;
        var wasPaused = _paused;
        _paused = true;
        _gate.Cancel(); _hint.Hide(); _notice.Hide(); CancelDelivery(false);
        try
        {
            AppPaths.UnregisterConnection();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            LocalLog.Write("unregister", e);
            _paused = wasPaused;
            UpdateActivityTimer();
            Notify("移除快投连接失败，程序仍可继续使用。请检查目录和注册表权限后重试。" + Environment.NewLine + e.Message);
            return;
        }
        MessageBox.Show("已移除当前程序的浏览器连接及对应的开机自启。\n\n请到 Edge 扩展页移除快投扩展，再删除本程序文件夹。设置和绑定数据已保留。",
            "快投 QuickShot", MessageBoxButtons.OK, MessageBoxIcon.Information);
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _gate.Cancel(); CancelDelivery(false); _disposed = true; _timer.Stop(); _timer.Dispose(); _keyboard.Dispose(); _mouse.Dispose(); _clipboard.Dispose();
            _server.Dispose(); _tray.Visible = false; _tray.Dispose(); _icon.Dispose(); _hint.Dispose(); _notice.Dispose(); _dashboard?.Dispose(); _dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
