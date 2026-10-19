using System.Runtime.InteropServices;
using QuickShot.Core;
using Shortcut = QuickShot.Core.Shortcut;

namespace QuickShot.App;

internal sealed class KeyboardMonitor : IDisposable
{
    private readonly NativeMethods.HookProc _callback;
    private readonly HashSet<uint> _suppressed = [];
    private readonly HashSet<uint> _down = [];
    private readonly HashSet<uint> _heldAtCapture = [];
    private ShortcutModifiers _modifiers;
    private nint _hook;
    private bool _requested;
    internal Func<KeyStroke, bool>? KeyInput { get; set; }
    internal bool KeyInputEnabled { get; set; }
    internal bool Enabled
    {
        get => _hook != 0;
        set { _requested = value; UpdateHook(); }
    }

    internal KeyboardMonitor()
    {
        _callback = Callback;
    }

    private void UpdateHook()
    {
        if (_requested && _hook == 0)
        {
            _hook = NativeMethods.SetWindowsHookEx(13, _callback, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            ReadPressedKeys();
        }
        else if (!_requested && _suppressed.Count == 0 && _hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook); _hook = 0;
            _down.Clear(); _heldAtCapture.Clear(); _modifiers = ShortcutModifiers.None;
        }
    }

    private void ReadPressedKeys()
    {
        _down.Clear();
        for (uint key = 7; key < 255; key++)
            if (key is not (0x10 or 0x11 or 0x12) && NativeMethods.IsDown((int)key)) _down.Add(key);
        UpdateModifiers();
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code < 0) return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(lParam);
        if ((data.Flags & 0x10) != 0) return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        var down = wParam == NativeMethods.WmKeyDown || wParam == NativeMethods.WmSysKeyDown;
        var up = wParam == NativeMethods.WmKeyUp || wParam == NativeMethods.WmSysKeyUp;
        var key = data.VkCode switch
        {
            0x10 => data.ScanCode == 0x36 ? 0xA1u : 0xA0u,
            0x11 => (data.Flags & 1) != 0 ? 0xA3u : 0xA2u,
            0x12 => (data.Flags & 1) != 0 ? 0xA5u : 0xA4u,
            _ => data.VkCode
        };
        var repeat = down && !_down.Add(key);
        if (up) _down.Remove(key);
        if (Shortcut.IsModifier(key)) UpdateModifiers();
        var modifiers = _modifiers;
        var heldAtCapture = _heldAtCapture.Contains(key);
        if (up) _heldAtCapture.Remove(key);
        if (up)
        {
            if (_suppressed.Remove(key)) { UpdateHook(); return 1; }
        }
        if (down && _suppressed.Contains(key)) return 1;
        if (KeyInputEnabled && (down || up) && KeyInput?.Invoke(new KeyStroke(key, modifiers, down, repeat, heldAtCapture)) == true)
        {
            if (down) _suppressed.Add(key);
            return 1;
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    internal bool AllDeliveryKeysReleased
    {
        get
        {
            if (_suppressed.Count != 0 || NativeMethods.HasModifiers) return false;
            foreach (var key in _down) if (NativeMethods.IsDown((int)key)) return false;
            return true;
        }
    }
    internal void MarkImageReady()
    {
        Enabled = true;
        _heldAtCapture.Clear(); _heldAtCapture.UnionWith(_down);
    }
    private void UpdateModifiers()
    {
        _modifiers = ShortcutModifiers.None;
        foreach (var pressed in _down) _modifiers |= Shortcut.ModifierFor(pressed);
    }
    public void Dispose() { _requested = false; _suppressed.Clear(); UpdateHook(); }
}

