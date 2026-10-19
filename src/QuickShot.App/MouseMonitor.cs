namespace QuickShot.App;

internal sealed class MouseMonitor : IDisposable
{
    private readonly NativeMethods.HookProc _callback;
    private nint _hook;
    internal Action? Clicked { get; set; }
    internal MouseMonitor()
    {
        _callback = (code, message, data) =>
        {
            if (code >= 0 && (int)message is 0x0201 or 0x0204 or 0x0207 or 0x020B) Clicked?.Invoke();
            return NativeMethods.CallNextHookEx(_hook, code, message, data);
        };
    }
    internal bool Enabled
    {
        get => _hook != 0;
        set
        {
            if (value == Enabled) return;
            if (value)
            {
                var hook = NativeMethods.SetWindowsHookEx(14, _callback, NativeMethods.GetModuleHandle(null), 0);
                if (hook == 0) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                _hook = hook;
            }
            else
            {
                if (!NativeMethods.UnhookWindowsHookEx(_hook)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                _hook = 0;
            }
        }
    }
    public void Dispose() { if (_hook != 0) NativeMethods.UnhookWindowsHookEx(_hook); _hook = 0; }
}
