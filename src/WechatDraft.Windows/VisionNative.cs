using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using WechatDraft.Core;

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

    internal static INPUT[] PrepareUnicodeInput(string text)
    {
        if (text.Length is 0 or > 4096 || text.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029'))
            throw new InvalidOperationException("祝福正文无法直接输入，已保留正文供手动处理。");

        // VK_PACKET carries text. In particular, this batch contains no Enter, paste, or copy key.
        var inputs = new INPUT[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            var input = new INPUT { Type = 1, Data = new InputUnion
            {
                Keyboard = new KeyboardInput { Scan = text[i], Flags = 0x4, ExtraInfo = VisionInputTag }
            } };
            inputs[2 * i] = input;
            input.Data.Keyboard.Flags |= 0x2;
            inputs[2 * i + 1] = input;
        }
        return inputs;
    }

    internal static void VisionChord(ushort key)
    {
        if (key is not (0x41 or 0x43 or 0x23 or 0x24 or 0x56)) throw new InvalidOperationException("不允许的视觉键盘动作。");
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
    internal static void VisionCopyAndCollapse()
    {
        // One input batch keeps copy/end adjacent and never includes Enter.
        var keys = new[] { Key(0x11), Key(0x43), Key(0x43, true), Key(0x23), Key(0x23, true), Key(0x11, true) };
        foreach (ref var input in keys.AsSpan()) input.Data.Keyboard.ExtraInfo = VisionInputTag;
        try { Keys(keys); }
        finally
        {
            var release = Key(0x11, true); release.Data.Keyboard.ExtraInfo = VisionInputTag;
            SendInput(1, new[] { release }, Marshal.SizeOf<INPUT>());
        }
    }
    internal static void VisionPage(ushort key, int presses)
    {
        if (key is not (0x21 or 0x22)) throw new InvalidOperationException("不允许的翻页按键。");
        var down = Key(key);
        var up = Key(key, true);
        down.Data.Keyboard.Flags |= 1; // Extended navigation key, not the numeric keypad.
        up.Data.Keyboard.Flags |= 1;
        down.Data.Keyboard.ExtraInfo = up.Data.Keyboard.ExtraInfo = VisionInputTag;
        var keys = new INPUT[presses * 2];
        for (var i = 0; i < keys.Length; i += 2)
        {
            keys[i] = down;
            keys[i + 1] = up;
        }
        try { Keys(keys); }
        catch
        {
            SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
            throw;
        }
    }
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
    public TimeSpan QuietFor => _state.QuietFor;
    public string? LastActivity => _state.LastActivity;
    public string LastActivityDescription => _state.LastActivityDescription;

    public VisionInputMonitor(nint target, bool stopOnWheel = true, bool stopOnClick = true, bool stopOnKeyboard = true,
        nint companion = 0, bool trackWheelActivity = true)
    {
        _state = new VisionInteractionState(stopOnWheel, stopOnClick, stopOnKeyboard, trackWheelActivity: trackWheelActivity);
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
        var debug = DebugSession.Current;
        _foreground = (_, _, hwnd, _, _, _, _) =>
        {
            var actual = Native.GetForegroundWindow(); var changed = _state.Changed;
            _state.Foreground(hwnd, actual, target, companion);
            if (!changed && _state.Changed && _state.Reason == "foreground-change" && debug is { Enabled: true })
                debug.Event("visual-foreground-change", new { reported = DescribeWindow(hwnd), actual = DescribeWindow(actual),
                    target = target.ToInt64(), companion = companion.ToInt64() });
        };
        var module = Native.GetModuleHandle(null);
        _keyboardHook = Native.SetWindowsHookEx(13, _keyboard, module, 0);
        _mouseHook = Native.SetWindowsHookEx(14, _mouse, module, 0);
        _foregroundHook = Native.SetWinEventHook(3, 3, 0, _foreground, 0, 0, 0);
        if (_keyboardHook == 0 || _mouseHook == 0 || _foregroundHook == 0)
        { Dispose(); throw new InvalidOperationException("无法监测用户操作，已停止预填。"); }
    }
    private static object DescribeWindow(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid); var name = "";
        try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; } catch { }
        return new { handle = hwnd.ToInt64(), processId = pid, processName = name, owner = Native.GetAncestor(hwnd, 3).ToInt64() };
    }
    public void Dispose()
    {
        if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
        if (_foregroundHook != 0) Native.UnhookWinEvent(_foregroundHook);
        _keyboardHook = _mouseHook = _foregroundHook = 0;
    }
}
