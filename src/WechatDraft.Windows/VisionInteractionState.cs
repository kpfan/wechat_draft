using System;
using System.Threading;

namespace WechatDraft.Windows;

// Classify interruption without retaining key codes, typed text, or an input history.
internal sealed class VisionInteractionState(bool stopOnWheel = true, bool stopOnClick = true, bool stopOnKeyboard = true,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _lastActivityTimestamp = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private enum Cause { None, Keyboard, MouseClick, MouseWheel, Foreground }
    private int _cause;
    private int _wheelObserved;
    private int _activityVersion;
    private int _lastActivity;
    public bool Changed => Volatile.Read(ref _cause) != 0;
    public bool WheelObserved => Volatile.Read(ref _wheelObserved) != 0;
    public int ActivityVersion => Volatile.Read(ref _activityVersion);
    public TimeSpan QuietFor => _time.GetElapsedTime(Interlocked.Read(ref _lastActivityTimestamp));
    public string? LastActivity => (Cause)Volatile.Read(ref _lastActivity) switch
    { Cause.Keyboard => "keyboard-input", Cause.MouseClick => "mouse-click", Cause.MouseWheel => "mouse-wheel", _ => null };
    public string LastActivityDescription => (Cause)Volatile.Read(ref _lastActivity) switch
    { Cause.Keyboard => "键盘输入", Cause.MouseClick => "鼠标点击", Cause.MouseWheel => "鼠标滚轮操作", _ => "用户操作" };
    public string? Reason => (Cause)Volatile.Read(ref _cause) switch
    {
        Cause.Keyboard => "keyboard-input", Cause.MouseClick => "mouse-click", Cause.MouseWheel => "mouse-wheel",
        Cause.Foreground => "foreground-change", _ => null
    };
    public string Description => (Cause)Volatile.Read(ref _cause) switch
    {
        Cause.Keyboard => "检测到键盘输入", Cause.MouseClick => "检测到鼠标点击", Cause.MouseWheel => "检测到鼠标滚轮操作",
        Cause.Foreground => "检测到窗口焦点切换", _ => "输入状态发生变化"
    };

    public void Keyboard(bool ownInput)
    {
        if (ownInput) return;
        Interlocked.Exchange(ref _lastActivityTimestamp, _time.GetTimestamp());
        Volatile.Write(ref _lastActivity, (int)Cause.Keyboard);
        Interlocked.Increment(ref _activityVersion);
        if (stopOnKeyboard) Set(Cause.Keyboard);
    }

    public void Mouse(uint message, bool ownInput)
    {
        if (ownInput) return;
        if (message is 0x201 or 0x204 or 0x207 or 0x20B)
        {
            Interlocked.Exchange(ref _lastActivityTimestamp, _time.GetTimestamp());
            Volatile.Write(ref _lastActivity, (int)Cause.MouseClick);
            Interlocked.Increment(ref _activityVersion);
            if (stopOnClick) Set(Cause.MouseClick);
        }
        else if (message is 0x20A or 0x20E)
        {
            Interlocked.Exchange(ref _lastActivityTimestamp, _time.GetTimestamp());
            Volatile.Write(ref _lastActivity, (int)Cause.MouseWheel);
            Interlocked.Exchange(ref _wheelObserved, 1);
            Interlocked.Increment(ref _activityVersion);
            if (stopOnWheel) Set(Cause.MouseWheel);
        }
        // Mouse movement and button/key releases do not invalidate an input task.
    }

    public void Foreground(nint reportedWindow, nint actualWindow, nint target, nint companion = 0)
    {
        if (reportedWindow != 0 && reportedWindow == actualWindow)
            Interlocked.Exchange(ref _lastActivityTimestamp, _time.GetTimestamp());
        // Discard stale queued events from the startup handover to WeChat.
        if (IsUnexpectedForeground(reportedWindow, actualWindow, target, companion)) Set(Cause.Foreground);
    }

    internal static bool IsAllowedForeground(nint actualWindow, nint target, nint companion = 0) =>
        actualWindow == target || (companion != 0 && actualWindow == companion);

    internal static bool IsUnexpectedForeground(nint reportedWindow, nint actualWindow, nint target, nint companion = 0) =>
        reportedWindow != 0 && reportedWindow != target && !IsAllowedForeground(actualWindow, target, companion);

    private void Set(Cause cause) => Interlocked.CompareExchange(ref _cause, (int)cause, 0);
}
