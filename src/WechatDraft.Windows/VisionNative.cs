using System;
using System.Runtime.InteropServices;

namespace WechatDraft.Windows;

internal static partial class Native
{
    internal const nuint VisionInputTag = 0x57445631;
    internal delegate nint LowLevelProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardHookData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseHookData { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] internal static extern nint SetWindowsHookEx(int hook, LowLevelProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint hwnd, out Rect rectangle);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);

    internal static void VisionChord(ushort key)
    {
        if (key is not (0x41 or 0x43 or 0x23 or 0x56)) throw new InvalidOperationException("不允许的视觉键盘动作。");
        var keys = new[] { Key(0x11), Key(key), Key(key, true), Key(0x11, true) };
        for (var i = 0; i < keys.Length; i++) keys[i].Data.Keyboard.ExtraInfo = VisionInputTag;
        try { Keys(keys); }
        catch
        {
            // Release possibly injected modifiers without replaying the original chord.
            var releases = new[] { Key(key, true), Key(0x11, true) };
            for (var i = 0; i < releases.Length; i++) releases[i].Data.Keyboard.ExtraInfo = VisionInputTag;
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<INPUT>());
            throw;
        }
    }
    internal static void VisionClick(int x, int y)
    {
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("无法定位微信输入光标。");
        Keys(new INPUT { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 2, ExtraInfo = VisionInputTag } } },
             new INPUT { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 4, ExtraInfo = VisionInputTag } } });
    }
    internal static void VisionWheel(int delta) => Keys(new INPUT
    { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 0x800, MouseData = unchecked((uint)delta), ExtraInfo = VisionInputTag } } });
}

internal sealed class VisionInputMonitor : IDisposable
{
    private readonly Native.LowLevelProc _keyboard;
    private readonly Native.LowLevelProc _mouse;
    private readonly Native.WinEventProc _foreground;
    private nint _keyboardHook, _mouseHook, _foregroundHook;
    private readonly VisionInteractionState _state;
    public bool Changed => _state.Changed;
    public string? Reason => _state.Reason;
    public string Description => _state.Description;
    public bool WheelObserved => _state.WheelObserved;
    public int ActivityVersion => _state.ActivityVersion;

    public VisionInputMonitor(nint target, bool stopOnWheel = true, bool stopOnClick = true, bool stopOnKeyboard = true, nint companion = 0)
    {
        _state = new VisionInteractionState(stopOnWheel, stopOnClick, stopOnKeyboard);
        _keyboard = (code, message, data) =>
        {
            if (code >= 0 && message is 0x100 or 0x104)
                _state.Keyboard(Marshal.PtrToStructure<Native.KeyboardHookData>(data).Extra == Native.VisionInputTag);
            return Native.CallNextHookEx(0, code, message, data);
        };
        _mouse = (code, message, data) =>
        {
            if (code >= 0 && message is 0x201 or 0x204 or 0x207 or 0x20A or 0x20B or 0x20E)
                _state.Mouse((uint)message, Marshal.PtrToStructure<Native.MouseHookData>(data).Extra == Native.VisionInputTag);
            return Native.CallNextHookEx(0, code, message, data);
        };
        _foreground = (_, _, hwnd, _, _, _, _) => _state.Foreground(hwnd, Native.GetForegroundWindow(), target, companion);
        var module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(13, _keyboard, module, 0);
        _mouseHook = Native.SetWindowsHookEx(14, _mouse, module, 0);
        _foregroundHook = Native.SetWinEventHook(3, 3, 0, _foreground, 0, 0, 0);
        if (_keyboardHook == 0 || _mouseHook == 0 || _foregroundHook == 0)
        { Dispose(); throw new InvalidOperationException("无法监测用户操作，已停止预填。"); }
    }
    public void Dispose()
    {
        if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
        if (_foregroundHook != 0) Native.UnhookWinEvent(_foregroundHook);
        _keyboardHook = _mouseHook = _foregroundHook = 0;
    }
}
