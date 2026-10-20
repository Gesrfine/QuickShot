using System.Runtime.InteropServices;

namespace QuickShot.App;

internal static class ForegroundVisibility
{
    private delegate void WinEvent(nint hook, uint eventId, nint window, int objectId, int childId, uint thread, uint time);
    private static readonly WinEvent Changed = (_, _, _, _, _, _, _) =>
    {
        if (_window != 0 && NativeMethods.GetForegroundWindow() != _window) Restore();
    };
    private static nint _window, _hook;

    internal static bool EnsureVisible(nint window)
    {
        if (NativeMethods.GetForegroundWindow() != window) return false;
        if (_window != 0 && _window != window) Restore();
        if (IsUnobscured(window)) return true;
        if (_window == window || (GetWindowLong(window, -20) & 8) != 0)
        {
            NativeMethods.RaiseWithoutActivation(window);
            return IsUnobscured(window);
        }
        _hook = SetWinEventHook(3, 3, 0, Changed, 0, 0, 0);
        if (_hook == 0) return false;
        _window = window;
        NativeMethods.RaiseWithoutActivation(window);
        if (NativeMethods.GetForegroundWindow() == window) return IsUnobscured(window);
        Restore();
        return false;
    }

    internal static bool IsUnobscured(nint window)
    {
        if (!GetWindowRect(window, out var bounds)) return false;
        var point = new Point(bounds.Left + (bounds.Right - bounds.Left) / 2, bounds.Top + (bounds.Bottom - bounds.Top) / 2);
        var covering = GetAncestor(WindowFromPoint(point), 2);
        for (var depth = 0; covering != 0 && depth < 32; depth++)
        {
            if (covering == window) return true;
            covering = GetWindow(covering, 4);
        }
        return false;
    }

    internal static void Restore()
    {
        var window = _window; _window = 0;
        var hook = _hook; _hook = 0;
        if (hook != 0) UnhookWinEvent(hook);
        if (window != 0) SetWindowPos(window, new nint(-2), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x4000);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
