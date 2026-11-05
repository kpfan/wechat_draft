using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WechatDraft.Core;

namespace WechatDraft.Windows;

// Read-only hooks and in-memory screen observations. Never clicks, types, copies, or sends.
public sealed class WechatSentGreetingMonitor : IDisposable
{
    private sealed record Frame(Capture Capture, long Tick);
    private sealed class Pending(Frame before, string trigger)
    {
        public readonly Frame Before = before;
        public readonly string Trigger = trigger;
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly DateTimeOffset ObservedAt = DateTimeOffset.UtcNow;
        public readonly long Tick = Environment.TickCount64;
        public bool Ambiguous;
        public Capture? Last;
    }
    private readonly nint _target;
    private readonly string _identity;
    private readonly VisionWindowFrame _frame;
    private readonly VisionLayout _layout;
    private readonly VisionRegionSnapshot _header;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 350 };
    private readonly Native.LowLevelProc _mouse, _keyboard;
    private readonly SendGestureState _gestures = new();
    private nint _mouseHook, _keyboardHook;
    private Frame? _latest;
    private Pending? _pending;
    private bool _editorFocused, _capturing;
    private volatile bool _paused, _disposed;
    private int _epoch;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string DisplayName => _layout.Title.Trim();
    public bool IsRunning => !_disposed;
    public event Action<SendObservation>? Observed;
    public event Action<string>? StatusChanged;

    public static async Task<WechatSentGreetingMonitor> StartAsync(nint target, AppSettings settings,
        IWindowVisionModel model, ISecretProtector protector, CancellationToken ct)
    {
        using var adapter = new WechatVisionAdapter(target, 0, false, settings, model, protector);
        await adapter.CheckLayoutAsync(ct);
        ct.ThrowIfCancellationRequested();
        var baseline = adapter.CopyObservationBaseline();
        using var image = baseline.Image;
        return new WechatSentGreetingMonitor(target, baseline.Frame, baseline.Layout, image, baseline.Identity);
    }

    private WechatSentGreetingMonitor(nint target, VisionWindowFrame frame, VisionLayout layout, Bitmap baseline, string identity)
    {
        _target = target; _frame = frame; _layout = layout; _identity = identity;
        _header = VisionRegionSnapshot.Capture(baseline, frame.ToClientRectangle(layout.Header!));
        _mouse = MouseHook; _keyboard = KeyboardHook;
        try
        {
            // Validate the live title and geometry against the frozen layout before installing hooks.
            if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("请保持目标微信前台，重新开启记录。");
            _latest = CaptureFrame();
            var module = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(14, _mouse, module, 0);
            _keyboardHook = Native.SetWindowsHookEx(13, _keyboard, module, 0);
            if (_mouseHook == 0 || _keyboardHook == 0) throw new InvalidOperationException("无法监听发送线索，未开启记录。");
            _timer.Tick += async (_, _) => await TickAsync();
            _timer.Start();
        }
        catch { Dispose(); throw; }
    }

    public void Pause(bool paused)
    {
        _epoch++; _paused = paused; _latest = null; _editorFocused = false; _gestures.Reset();
        var pending = _pending; _pending = null;
        if (pending is not null) Emit(pending, pending.Before.Capture, "记录暂停，未能完成这次发送核对。请检查微信。");
    }

    private string Identity()
    {
        Native.GetWindowThreadProcessId(_target, out var pid);
        using var process = Process.GetProcessById((int)pid);
        return $"{pid}:{process.StartTime.ToUniversalTime().Ticks}:{_target}";
    }

    private bool Active => !_disposed && !_paused && Native.GetForegroundWindow() == _target;
    private bool Inside(Native.Point point, VisionBox box)
    {
        var rectangle = _frame.ToClientRectangle(box); rectangle.Offset(_frame.Bounds.Location);
        return rectangle.Contains(point.X, point.Y) && Native.GetAncestor(Native.WindowFromPoint(point), 2) == _target;
    }
    private nint MouseHook(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && message is 0x201 or 0x202)
            {
                var mouse = Marshal.PtrToStructure<Native.MouseHookData>(data);
                var own = mouse.Extra == Native.VisionInputTag || (mouse.Flags & 1) != 0;
                var insideSend = Active && Inside(mouse.Point, _layout.Send!);
                if (message == 0x201 && !own && !insideSend)
                    _editorFocused = Active && Inside(mouse.Point, _layout.Editor!);
                if (_gestures.Mouse(message == 0x201, insideSend, Active, own)) Intent("鼠标发送");
            }
        }
        catch { _latest = null; _gestures.Reset(); }
        return Native.CallNextHookEx(0, code, message, data);
    }
    private nint KeyboardHook(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && message is 0x100 or 0x104 or 0x101 or 0x105)
            {
                var key = Marshal.PtrToStructure<Native.KeyboardHookData>(data);
                var down = message is 0x100 or 0x104;
                if (key.Key == 9 && down) _editorFocused = false;
                if (key.Key == 13)
                {
                    var forbidden = new[] { 0x10, 0x12, 0x5B, 0x5C, 0xE5 }.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0);
                    var own = key.Extra == Native.VisionInputTag || (key.Flags & 0x10) != 0;
                    if (_gestures.Enter(down, Active, _editorFocused, own, forbidden)) Intent("键盘发送线索");
                }
            }
        }
        catch { _latest = null; _gestures.Reset(); }
        return Native.CallNextHookEx(0, code, message, data);
    }

    private void Intent(string trigger)
    {
        // Hooks only set local state; screen capture, network, and persistence run outside the hook.
        if (_pending is not null) { _pending.Ambiguous = true; return; }
        if (_latest is null || Environment.TickCount64 - _latest.Tick > 900) return;
        _pending = new Pending(_latest, trigger);
    }

    private async Task TickAsync()
    {
        if (_disposed || _paused || _capturing) return;
        _capturing = true;
        var epoch = _epoch;
        try
        {
            if (!Native.IsWindow(_target) || Identity() != _identity) throw new InvalidOperationException("微信进程或窗口已变化，记录已停止。");
            if (!Active)
            {
                if (_pending is not null) throw new InvalidOperationException("发送后切换了窗口，未能核对本次消息，记录已停止。");
                _latest = null; _editorFocused = false; _gestures.Reset(); return;
            }
            var frame = await Task.Run(CaptureFrame);
            if (_disposed || _paused || epoch != _epoch) return;
            if (_pending is { } pending)
            {
                var elapsed = Environment.TickCount64 - pending.Tick;
                var changed = !pending.Before.Capture.PngBytes.AsSpan().SequenceEqual(frame.Capture.PngBytes);
                var stable = pending.Last is not null && pending.Last.PngBytes.AsSpan().SequenceEqual(frame.Capture.PngBytes);
                if (changed && ((stable && elapsed >= 700) || elapsed >= 3500))
                {
                    _pending = null;
                    Emit(pending, frame.Capture, pending.Ambiguous ? "短时间内检测到多次发送线索，未自动关联正文。" :
                        !stable ? "消息画面持续变化，未自动关联正文。" : "");
                }
                else if (elapsed >= 3500)
                {
                    _pending = null; StatusChanged?.Invoke("未观察到消息区变化，本次未请求模型或保存正文。");
                }
                else pending.Last = frame.Capture;
            }
            _latest = frame;
        }
        catch (Exception e)
        {
            if (_disposed || epoch != _epoch) return;
            if (!Active && _pending is null)
            { _latest = null; _editorFocused = false; _gestures.Reset(); return; }
            var detail = e.Message;
            try
            {
                if (_pending is { } pending) { _pending = null; Emit(pending, pending.Before.Capture, detail); }
            }
            catch (Exception) { detail += " 本次待确认信息未能保存。"; }
            finally { Dispose(); }
            StatusChanged?.Invoke(detail);
        }
        finally { _capturing = false; }
    }

    private void Emit(Pending pending, Capture after, string uncertainty) => Observed?.Invoke(new(
        pending.Id, SessionId, DisplayName, pending.ObservedAt, pending.Trigger, pending.Before.Capture, after, uncertainty));

    private Frame CaptureFrame()
    {
        if (!Active || Native.IsIconic(_target) || Identity() != _identity) throw new InvalidOperationException("微信状态已变化，记录已停止。");
        var point = new Native.Point();
        if (!Native.GetClientRect(_target, out var bounds) || !Native.ClientToScreen(_target, ref point))
            throw new InvalidOperationException("无法读取微信窗口范围，记录已停止。");
        var actual = new Rectangle(point.X, point.Y, bounds.Width, bounds.Height);
        if (!_frame.Matches(actual, Native.GetDpiForWindow(_target)))
            throw new InvalidOperationException("微信窗口移动、大小或缩放变化，请重新开启记录。");
        var desktop = new Rectangle(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
        if (!desktop.Contains(actual)) throw new InvalidOperationException("微信窗口未完整显示，记录已停止。");
        using var header = CaptureRegion(_frame.ToClientRectangle(_layout.Header!));
        if (!_header.Matches(VisionRegionSnapshot.Capture(header, new Rectangle(0, 0, header.Width, header.Height)), false))
            throw new InvalidOperationException("聊天标题画面变化，请为新的聊天重新开启记录。");
        using var messages = CaptureRegion(_frame.ToClientRectangle(_layout.Messages!));
        var scale = Math.Min(4d, 1800d / header.Width);
        var titleWidth = (int)(header.Width * scale); var titleHeight = (int)(header.Height * scale);
        using var composite = new Bitmap(Math.Max(messages.Width, titleWidth + 24), messages.Height + titleHeight + 24);
        using (var graphics = Graphics.FromImage(composite))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(header, new Rectangle(12, 8, titleWidth, titleHeight));
            graphics.DrawImageUnscaled(messages, 0, titleHeight + 24);
        }
        var afterPoint = new Native.Point();
        if (!Active || Identity() != _identity || !Native.GetClientRect(_target, out var afterBounds) ||
            !Native.ClientToScreen(_target, ref afterPoint) ||
            !_frame.Matches(new Rectangle(afterPoint.X, afterPoint.Y, afterBounds.Width, afterBounds.Height), Native.GetDpiForWindow(_target)))
            throw new InvalidOperationException("采集期间微信目标或布局变化，记录已停止。");
        using var stream = new MemoryStream(); composite.Save(stream, ImageFormat.Png);
        return new(new Capture("chat", stream.ToArray()), Environment.TickCount64);
    }

    private Bitmap CaptureRegion(Rectangle local)
    {
        var screen = local; screen.Offset(_frame.Bounds.Location);
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 5; col++)
            {
                var point = new Native.Point { X = screen.Left + 1 + (screen.Width - 3) * col / 4,
                    Y = screen.Top + 1 + (screen.Height - 3) * row / 2 };
                if (Native.GetAncestor(Native.WindowFromPoint(point), 2) != _target)
                    throw new InvalidOperationException("聊天区域被其他窗口遮挡，记录已停止。");
            }
        var bitmap = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _epoch++; _timer.Stop(); _timer.Dispose();
        if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = 0; _latest = null; _pending = null;
    }
}
