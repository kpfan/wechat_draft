using System;
using System.Runtime.InteropServices;

namespace WechatDraft.Windows;

public sealed class GlobalHotkey : IDisposable
{
    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    private readonly nint _hwnd;
    private readonly WindowProc _handler;
    private readonly nint _previous;
    private bool _registered;
    public event Action? Pressed;
    public GlobalHotkey(nint hwnd)
    {
        _hwnd = hwnd; _handler = Handle;
        _previous = Native.SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(_handler));
    }
    public bool Register(uint key)
    {
        if (_registered) Native.UnregisterHotKey(_hwnd, 0x4259);
        _registered = Native.RegisterHotKey(_hwnd, 0x4259, 0x0001 | 0x0002 | 0x4000, key);
        return _registered;
    }
    public void Pause() { if (_registered) Native.UnregisterHotKey(_hwnd, 0x4259); _registered = false; }
    private nint Handle(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x0312 && wParam == 0x4259) { Pressed?.Invoke(); return 0; }
        return Native.CallWindowProc(_previous, hwnd, message, wParam, lParam);
    }
    public void Dispose() { Pause(); Native.SetWindowLongPtr(_hwnd, -4, _previous); GC.KeepAlive(_handler); }
}
