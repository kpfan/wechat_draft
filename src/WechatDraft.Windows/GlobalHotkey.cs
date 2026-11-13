using System;
using System.Runtime.InteropServices;
using WechatDraft.Core;

namespace WechatDraft.Windows;

public sealed class GlobalHotkey : IDisposable
{
    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    private readonly nint _hwnd;
    private readonly WindowProc _handler;
    private readonly nint _previous;
    private const int FirstId = 0x4259;
    private const int SecondId = 0x425A;
    private const uint NoRepeat = 0x4000;
    private (HotkeyShortcut Shortcut, int Id)? _registration;
    public bool IsRegistered => _registration.HasValue;
    public event Action? Pressed;
    public GlobalHotkey(nint hwnd)
    {
        _hwnd = hwnd; _handler = Handle;
        _previous = Native.SetWindowLongPtr(hwnd, -4, Marshal.GetFunctionPointerForDelegate(_handler));
    }
    public bool Register(HotkeyShortcut shortcut, Action? saveSettings = null)
    {
        if (!shortcut.IsValid) return false;
        if (_registration?.Shortcut == shortcut)
        {
            saveSettings?.Invoke();
            return true;
        }

        // Keep the old combination active until both registration and persistence succeed.
        var id = _registration?.Id == FirstId ? SecondId : FirstId;
        if (!Native.RegisterHotKey(_hwnd, id, (uint)shortcut.Modifiers | NoRepeat, shortcut.VirtualKey))
            return false;
        try
        {
            saveSettings?.Invoke();
        }
        catch
        {
            Native.UnregisterHotKey(_hwnd, id);
            throw;
        }
        Pause();
        _registration = (shortcut, id);
        return true;
    }

    public void Pause()
    {
        if (_registration is { } registration) Native.UnregisterHotKey(_hwnd, registration.Id);
        _registration = null;
    }
    private nint Handle(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == 0x0312 && _registration is { } registration && wParam == registration.Id)
        {
            var keys = (uint)lParam;
            if ((keys & 0xF) == (uint)registration.Shortcut.Modifiers && (keys >> 16) == registration.Shortcut.VirtualKey)
                Pressed?.Invoke();
            return 0;
        }
        return Native.CallWindowProc(_previous, hwnd, message, wParam, lParam);
    }
    public void Dispose() { Pause(); Native.SetWindowLongPtr(_hwnd, -4, _previous); GC.KeepAlive(_handler); }
}
