using QuickShot.Core;
using System.Runtime.InteropServices;
using Shortcut = QuickShot.Core.Shortcut;

namespace QuickShot.App;

internal sealed class DeliveryActivation : Form
{
    private readonly Dictionary<int, Shortcut> _registered = [];
    private IReadOnlyList<Shortcut>? _shortcuts;
    private int? _pending;
    private DateTimeOffset _pendingAt;
    internal Action<string>? Requested;

    internal DeliveryActivation()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 30, 44);
        ForeColor = Color.White;
        ClientSize = new Size(280, 48);
        Text = "QuickShot · 正在切换";
        Controls.Add(new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true });
    }

    internal void UpdateShortcuts(IReadOnlyList<Shortcut> shortcuts)
    {
        if (ReferenceEquals(_shortcuts, shortcuts)) return;
        foreach (var id in _registered.Keys) UnregisterHotKey(Handle, id);
        _registered.Clear(); _pending = null; _shortcuts = shortcuts;
        for (var index = 0; index < shortcuts.Count; index++)
        {
            var shortcut = shortcuts[index];
            var modifiers = 0x4000u;
            if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Alt)) modifiers |= 1;
            if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Ctrl)) modifiers |= 2;
            if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Shift)) modifiers |= 4;
            if (shortcut.Modifiers.HasFlag(ShortcutModifiers.Win)) modifiers |= 8;
            if (RegisterHotKey(Handle, index + 1, modifiers, shortcut.VirtualKey)) _registered.Add(index + 1, shortcut);
        }
    }

    internal bool ExpectHotkey(Shortcut shortcut)
    {
        foreach (var (id, registered) in _registered)
        {
            if (registered != shortcut) continue;
            _pending = id;
            _pendingAt = DateTimeOffset.UtcNow;
            return true;
        }
        return false;
    }

    internal void ClearPending() => _pending = null;

    internal string? TakePendingFallback(DateTimeOffset now)
    {
        if (_pending is not { } id || now - _pendingAt < TimeSpan.FromMilliseconds(300) || !_registered.TryGetValue(id, out var shortcut)) return null;
        _pending = null;
        return shortcut.Text;
    }

    private void ReceiveHotkey(int id)
    {
        if (_pending != id || !_registered.TryGetValue(id, out var shortcut)) return;
        _pending = null;
        Requested?.Invoke(shortcut.Text);
    }

    internal void BeginDelivery(string name, bool showNotification)
    {
        Controls[0].Text = "正在切换至 " + name + "…";
        Opacity = showNotification ? 1 : 0;
        var area = Screen.FromHandle(NativeMethods.GetForegroundWindow()).WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        Show();
        NativeMethods.TryActivateWindow(Handle);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312) { ReceiveHotkey((int)message.WParam); return; }
        base.WndProc(ref message);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) UpdateShortcuts(Array.Empty<Shortcut>());
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
}
