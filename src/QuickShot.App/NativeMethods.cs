using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace QuickShot.App;

internal static class NativeMethods
{
    internal const int WmClipboardUpdate = 0x031D;
    internal const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    internal const int VkControl = 0x11, VkShift = 0x10, VkAlt = 0x12, VkEscape = 0x1B;
    internal delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AddClipboardFormatListener(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveClipboardFormatListener(nint window);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    private delegate bool WindowEnumerator(nint window, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(WindowEnumerator callback, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int max);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] input, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardHookData
    {
        internal uint VkCode, ScanCode, Flags, Time;
        internal nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { internal uint Type; internal InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] internal KEYBDINPUT Keyboard;
        [FieldOffset(0)] internal MOUSEINPUT Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    {
        internal ushort Key, ScanCode;
        internal uint Flags, Time;
        internal nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    {
        internal int X, Y;
        internal uint MouseData, Flags, Time;
        internal nuint ExtraInfo;
    }

    internal static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static bool HasModifiers => IsDown(VkControl) || IsDown(VkShift) || IsDown(VkAlt) || IsDown(0x5B) || IsDown(0x5C);
    private static readonly uint PngFormat = RegisterClipboardFormat("PNG");
    internal static bool ClipboardHasImage => IsClipboardFormatAvailable(2) || IsClipboardFormatAvailable(8) || IsClipboardFormatAvailable(17) || IsClipboardFormatAvailable(PngFormat);

    internal static bool IsExpectedEdgeForeground(string token)
    {
        var window = GetForegroundWindow();
        if (window == 0 || string.IsNullOrEmpty(token)) return false;
        var title = new StringBuilder(2048);
        GetWindowText(window, title, title.Capacity);
        if (!title.ToString().Contains(token, StringComparison.Ordinal)) return false;
        GetWindowThreadProcessId(window, out var id);
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    internal static bool TryActivateEdgeWindow(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (IsExpectedEdgeForeground(token)) return ForegroundVisibility.EnsureVisible(GetForegroundWindow());
        var matches = new List<nint>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var title = new StringBuilder(2048); GetWindowText(window, title, title.Capacity);
            if (!title.ToString().Contains(token, StringComparison.Ordinal)) return true;
            GetWindowThreadProcessId(window, out var pid);
            try { using var process = Process.GetProcessById((int)pid); if (process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)) matches.Add(window); }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        if (matches.Count != 1) return false;
        var target = matches[0];
        return TryActivateWindow(target) && IsExpectedEdgeForeground(token) && ForegroundVisibility.EnsureVisible(target);
    }

    internal static bool TryActivateWindow(nint target)
    {
        if (target == 0) return false;
        if (IsHungAppWindow(target)) return false;
        if (GetForegroundWindow() == target) return true;
        if (IsIconic(target)) ShowWindowAsync(target, 9);
        SetForegroundWindow(target);
        return GetForegroundWindow() == target;
    }

    internal static void RaiseWithoutActivation(nint window) => SetWindowPos(window, new nint(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x4000);

    internal static bool Paste()
    {
        static INPUT Key(ushort code, bool up = false) => new() { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT { Key = code, ScanCode = (ushort)MapVirtualKey(code, 0), Flags = up ? 2u : 0u } } };
        INPUT[] input = [Key(VkControl), Key(0x56), Key(0x56, true), Key(VkControl, true)];
        var sent = SendInput((uint)input.Length, input, Marshal.SizeOf<INPUT>());
        if (sent == input.Length) return true;
        INPUT[] release = [Key(0x56, true), Key(VkControl, true)];
        SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>());
        return false;
    }
}

