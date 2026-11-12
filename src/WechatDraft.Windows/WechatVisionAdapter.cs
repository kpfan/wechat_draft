using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WechatDraft.Core;

namespace WechatDraft.Windows;

public sealed class WechatVisionAdapter(nint target, Action<TaskProgress>? progress = null, bool enableAppend = false,
    nint clipboardOwner = 0, nint companion = 0, IChatTitleOcr? titleOcr = null) : IConversationAdapter, IDisposable
{
    private readonly IChatTitleOcr _ocr = new ExactTitleOcrCache(titleOcr ?? WechatLocalTitleReader.Default);
    private Native.Rect _bounds;
    private VisionWindowFrame? _frame;
    private string _identity = "";
    private uint _pid;
    private VisionLayout? _layout;
    private VisionContextSnapshot? _frozenContext;
    private Capture? _scrolledContext;
    private bool _contextScrollHalted;
    private VisionAnchorHints? _anchors;
    private OcrPrefillGuard? _guard;
    private VisionInputChangeLog? _inputChanges;
    private DebugSession? _debug;
    private Rectangle? _knownEmptyHint;
    private VisionInputMonitor? _monitor;
    private VisionClipboard? _clipboard;
    private System.Windows.Forms.Timer? _watch;
    private bool _watchBusy, _disposed;
    private int _ioDepth;
    private nint _companion;
    private string _prefillReason = "";
    private readonly SemaphoreSlim _appendGate = new(1, 1);
    private DebugSession? Debug => _debug ?? DebugSession.Current;
    public bool CanAutoAppend => enableAppend && _guard is { Valid: true, WriteIssued: false };
    public string AutoAppendUnavailableReason => _prefillReason.Length > 0 ? _prefillReason : enableAppend
        ? "本地 OCR 或完整草稿未通过核对，请检查后复制正文。" : "预填已关闭。请检查后复制正文。";
    public string StateChangeReason => _guard?.Reason is { Length: > 0 } reason ? reason : _prefillReason;
    public string CleanupWarning { get; private set; } = "";
    public bool CanCollectMoreContext => !_disposed && !_contextScrollHalted && _monitor is not null;
    public string AutomationMethod => "vision";
    public int LayoutApiCallCount => 0;
    public byte[]? PreviewPng { get; private set; }

    internal (VisionWindowFrame Frame, VisionLayout Layout, Bitmap Image, string Identity) CopyObservationBaseline()
    {
        if (_frame is null || _layout is null || _frozenContext is null || string.IsNullOrWhiteSpace(_layout.Title))
            throw new InvalidOperationException("聊天画面及本地 OCR 标题尚未准备。");
        return (_frame, _layout, _frozenContext.CopyImage(), _identity);
    }
    private void Report(string stage, string detail, int percent)
    {
        DebugSession.Current?.Event("vision_progress", new { stage, detail, percent });
        progress?.Invoke(new(stage, detail, percent));
    }

    public async Task<VisionLayout> CheckLayoutAsync(CancellationToken ct)
    {
        if (!Native.IsWindow(target) || Native.IsIconic(target)) throw new InvalidOperationException("请打开并显示目标微信聊天窗口。");
        if (Native.GetForegroundWindow() != target) Native.SetForegroundWindow(target);
        if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("无法聚焦微信，请打开目标聊天后重新触发。");
        ct.ThrowIfCancellationRequested(); EnsureModifiersReleased();
        if (!TryBounds(out _bounds) || _bounds.Width < 400 || _bounds.Height < 350 || (long)_bounds.Width * _bounds.Height > 20000000)
            throw new InvalidOperationException("微信窗口尺寸不适合本地视觉定位。");
        Native.GetWindowThreadProcessId(target, out _pid);
        _identity = Identity(); var dpi = Native.GetDpiForWindow(target);
        if (dpi == 0) throw new InvalidOperationException("无法确认微信窗口显示缩放。");
        _frame = new(new Rectangle(_bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height), dpi);
        using var bitmap = Screenshot(ct, "initial_window");
        return await PrepareReadonlyFrameAsync(bitmap, dpi, ct);
    }
    internal async Task<VisionLayout> PrepareReadonlyFrameAsync(Bitmap bitmap, uint dpi, CancellationToken ct)
    {
        _frame ??= new(new Rectangle(0, 0, bitmap.Width, bitmap.Height), dpi);
        _frame.ValidateImageSize(bitmap.Size);
        _frozenContext?.Dispose(); _frozenContext = new VisionContextSnapshot(bitmap);
        Report("本地视觉定位", "正在匹配本地双工具栏与输入区分界", 11);
        return await LocateFrozenAsync(bitmap, dpi, ct);
    }
    internal async Task<VisionLayout> LocateFrozenAsync(Bitmap bitmap, uint dpi, CancellationToken ct)
    {
        PreviewPng = null; _layout = null; _anchors = null;
        (VisionLayout Layout, VisionAnchorHints Anchors) located;
        try { located = await Task.Run(() => WechatLocalLayout.Locate(bitmap, dpi, ct, _identity), ct); }
        catch (Exception e) { DebugSession.Current?.Event("local_layout_failed", new { type = e.GetType().Name, e.Message }); throw; }
        _layout = located.Layout; _anchors = located.Anchors;
        _frame ??= new(new Rectangle(0, 0, bitmap.Width, bitmap.Height), dpi);
        _frame.ValidateImageSize(bitmap.Size); ct.ThrowIfCancellationRequested();
        PreviewPng = Preview(bitmap, _layout);
        DebugSession.Current?.WriteJson("local_layout", new { method = "local-template-edge", layout = _layout, anchors = _anchors,
            window = _frame.Bounds, dpi, layoutApiCalls = 0, prefillRequested = enableAppend });
        DebugSession.Current?.WriteBytes("layout_preview", PreviewPng, ".png");
        return _layout;
    }
    internal async Task ReadLocalTitleAsync(CancellationToken ct)
    {
        using var image = (_frozenContext ?? throw new InvalidOperationException("聊天画面尚未准备。")).CopyImage();
        _layout = _layout! with { Title = OcrPrefillGuard.ValidateTitle(await ReadTitleAsync(image, ct)) };
    }
    private Task<string> ReadTitleAsync(Bitmap image, CancellationToken ct) => WechatLocalTitleReader.ReadAsync(image, _frame!, _layout!, ct, _ocr);

    public async Task<ConversationSnapshot> BeginAsync(CancellationToken ct)
    {
        _debug = DebugSession.Current;
        await CheckLayoutAsync(ct); await ReadLocalTitleAsync(ct);
        Native.GetWindowThreadProcessId(companion, out var ownerPid);
        _companion = companion != 0 && ownerPid == Environment.ProcessId ? companion : 0;
        try
        {
            _monitor = new VisionInputMonitor(target, stopOnWheel: false, stopOnClick: false, stopOnKeyboard: false,
                companion: _companion, trackWheelActivity: false);
            if (!enableAppend) return CreateReadonlySnapshot(ct);
            if (Debug is { Enabled: true } debug)
            {
                using var image = _frozenContext!.CopyImage();
                _inputChanges = new(CaptureInput(image, true), _frame!.Dpi, debug);
            }
            _guard = new(_layout!.Title);
            _watch = new System.Windows.Forms.Timer { Interval = 400 };
            _watch.Tick += async (_, _) => await ObserveAsync(ct);
            _watch.Start();
            return CreateReadonlySnapshot(ct) with { DirectAppend = true };
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
        {
            _prefillReason = "本次仅生成，未预填：" + e.Message;
            _monitor?.Dispose(); _monitor = null; _guard = null;
            DebugSession.Current?.Event("visual_prefill_unavailable", new { e.Message });
            Report("仅生成祝福", _prefillReason, 20);
            return CreateReadonlySnapshot(ct);
        }
    }
    internal ConversationSnapshot CreateReadonlySnapshot(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_frame is null || _layout is null || _frozenContext is null) throw new InvalidOperationException("本地视觉区域尚未准备。");
        var titleRead = !string.IsNullOrWhiteSpace(_layout.Title);
        return new(Guid.NewGuid().ToString("N"), _layout.Title, "", _identity, false, this,
            OriginalTextRead: false, DisplayNameRead: titleRead, TitleSource: titleRead ? "ocr" : "model");
    }

    private string Identity()
    {
        Native.GetWindowThreadProcessId(target, out var pid);
        using var process = Process.GetProcessById((int)pid);
        return $"{pid}:{process.StartTime.ToUniversalTime().Ticks}:{target}";
    }
    private bool TryBounds(out Native.Rect bounds)
    {
        bounds = default; var origin = new Native.Point();
        if (!Native.GetClientRect(target, out var client) || !Native.ClientToScreen(target, ref origin)) return false;
        bounds = new Native.Rect { Left = origin.X, Top = origin.Y, Right = origin.X + client.Width, Bottom = origin.Y + client.Height };
        return true;
    }
    private void EnsureWindow(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (_monitor?.Changed == true) throw new InvalidOperationException("切换到了其他程序，已停止本次视觉预填。");
        if (!Native.IsWindow(target) || Native.IsIconic(target) || Native.GetForegroundWindow() != target || Identity() != _identity ||
            !TryBounds(out var current) || _frame is null ||
            !_frame.Matches(new Rectangle(current.Left, current.Top, current.Width, current.Height), Native.GetDpiForWindow(target)))
            throw new InvalidOperationException("微信窗口、位置、尺寸或显示缩放已变化，请重新触发。");
    }
    private Bitmap Screenshot(CancellationToken ct, string? debugStage = null)
    {
        EnsureWindow(ct);
        var desktop = new Rectangle(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
        if (!desktop.Contains(_frame!.Bounds)) throw new InvalidOperationException("微信窗口部分位于屏幕之外，请完整显示后重试。");
        for (var row = 0; row < 5; row++)
            for (var col = 0; col < 7; col++)
                CheckPoint(_bounds.Left + 16 + (_bounds.Width - 32) * col / 6,
                    _bounds.Top + 16 + (_bounds.Height - 32) * row / 4, debugStage ?? "window-capture");
        var image = CaptureWindowPixels();
        try
        {
            EnsureWindow(ct); _frame.ValidateImageSize(image.Size);
            if (debugStage is not null) DebugImage(debugStage, image);
            return image;
        }
        catch { image.Dispose(); throw; }
    }
    private Bitmap CaptureWindowPixels()
    {
        var image = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, image.Size);
            return image;
        }
        catch { image.Dispose(); throw; }
    }
    private void CheckPoint(int x, int y, string stage)
    {
        var hit = Native.WindowFromPoint(new Native.Point { X = x, Y = y });
        var root = Native.GetAncestor(hit, 2);
        if (root == target) return;
        if (Debug is { Enabled: true } debug)
            RecordPointFailure(debug, x, y, stage, hit, root);
        throw new InvalidOperationException("无法确认微信窗口在检查点可见，已停止本次视觉操作。");
    }
    private void RecordPointFailure(DebugSession debug, int x, int y, string stage, nint hit, nint root)
    {
        var checkedUtc = DateTimeOffset.UtcNow;
        var localX = x - _bounds.Left;
        var localY = y - _bounds.Top;
        var foreground = Native.GetForegroundWindow();
        try
        {
            using var image = CaptureWindowPixels();
            using (var graphics = Graphics.FromImage(image))
            using (var outline = new Pen(Color.White, 6))
            using (var marker = new Pen(Color.Red, 3))
            {
                var circle = new Rectangle(localX - 12, localY - 12, 24, 24);
                graphics.DrawEllipse(outline, circle);
                graphics.DrawEllipse(marker, circle);
            }
            debug.WriteBytes("window-point-failed", Png(image), ".png");
        }
        catch (Exception e)
        {
            debug.Event("visual-window-point-image-failed", new { stage, type = e.GetType().Name });
        }
        debug.WriteJson("window-point-failed", new
        {
            stage, checkedUtc, screenPoint = new { x, y }, imagePoint = new { x = localX, y = localY },
            target = target.ToInt64(), foreground = foreground.ToInt64(), window = _frame!.Bounds,
            hit = hit.ToInt64(), root = root == 0 ? null : Native.DescribeWindow(root)
        });
    }
    private bool Reject(string reason)
    {
        if (_prefillReason.Length == 0) Debug?.Event("visual-prefill-rejected", new { reason });
        _prefillReason = reason; _guard?.Reject(reason); return false;
    }
    private async Task<bool> CheckLiveAsync(CancellationToken ct, bool observeInput = false)
    {
        EnsureWindow(ct);
        if (_guard is { Valid: false }) return false;
        using var current = Screenshot(ct);
        if (!await CheckTitleAsync(current, ct)) return false;
        // A timer that was awaiting OCR must not observe our own copy/paste in progress.
        if (observeInput && _ioDepth == 0 && _guard?.WriteIssued != true && _inputChanges is not null)
            _inputChanges.Observe(CaptureInput(current, true));
        return true;
    }
    private async Task<bool> CheckTitleAsync(Bitmap image, CancellationToken ct)
    {
        var title = await ReadTitleAsync(image, ct);
        EnsureWindow(ct);
        if (_guard is not null ? !_guard.CheckTitle(title) : title != _layout!.Title)
            return Reject("OCR 聊天标题已变化，未继续预填。");
        return true;
    }
    private VisionRegionSnapshot CaptureInput(Bitmap image, bool emptyDraft) => VisionRegionSnapshot.CaptureInput(image, _layout!, _frame!.Dpi,
        normalizeEmptyHint: emptyDraft, knownEmptyHint: _knownEmptyHint,
        hintObserved: hint => _knownEmptyHint = hint);
    private Task<VisionRegionSnapshot> WaitForInputSettledAsync(int version, CancellationToken ct, VisionRegionSnapshot changedFrom) =>
        VisionInputSettler.WaitAsync(async token =>
        {
            using var image = Screenshot(token);
            var title = await ReadTitleAsync(image, token);
            CheckActivity(version, token);
            if (_guard is null || !_guard.CheckTitle(title)) throw new InvalidOperationException(StateChangeReason);
            return CaptureInput(image, true);
        }, () => CheckActivity(version, ct), _frame!.Dpi, ct, changedFrom);
    private async Task ObserveAsync(CancellationToken ct)
    {
        if (_disposed || _watchBusy || _ioDepth > 0 || _guard is not { Valid: true, WriteIssued: false } || Native.GetForegroundWindow() != target) return;
        _watchBusy = true;
        try { await CheckLiveAsync(ct, observeInput: true); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (!_disposed)
            {
                Debug?.Event("visual-observation-failed", new { type = e.GetType().Name, e.Message });
                Reject(e.Message);
            }
        }
        catch (OperationCanceledException) { }
        finally { _watchBusy = false; }
    }
    private int CheckInputReady(CancellationToken ct)
    {
        EnsureWindow(ct);
        EnsureModifiersReleased();
        return _monitor?.ActivityVersion ?? 0;
    }
    private void CheckActivity(int version, CancellationToken ct)
    {
        EnsureWindow(ct);
        if ((_monitor?.ActivityVersion ?? 0) != version)
        {
            DebugSession.Current?.Event("visual-input-interrupted", new { expectedVersion = version, currentVersion = _monitor?.ActivityVersion,
                activity = _monitor?.LastActivity, writeIssued = _guard?.WriteIssued == true });
            throw new InvalidOperationException("复制或填写期间检测到" + (_monitor?.LastActivityDescription ?? "用户操作") + "，已停止本次预填。");
        }
    }
    private Point InputPoint()
    {
        var editor = _frame!.ToClientRectangle(_layout!.Editor!);
        if (editor.Width < 80 || editor.Height < 24) throw new InvalidOperationException("编辑区域太小，未点击。");
        var local = new Point(editor.Left + Math.Min(editor.Width / 3, (int)(_frame.Dpi * 40 / 96d)), editor.Top + Math.Min(editor.Height / 2, (int)(_frame.Dpi * 20 / 96d)));
        foreach (var box in new[] { _layout.Send, _layout.InputToolbar })
            if (box is not null)
            {
                var forbidden = _frame.ToClientRectangle(box); forbidden.Inflate(8, 8);
                if (forbidden.Contains(local)) throw new InvalidOperationException("编辑点接近工具栏或发送区域，未点击。");
            }
        return new(local.X + _frame.Bounds.X, local.Y + _frame.Bounds.Y);
    }
    private async Task<ClipboardDraftRead> ReadDraftAsync(VisionClipboard clipboard, int version, CancellationToken ct)
    {
        if (!_guard!.Valid) throw new InvalidOperationException(StateChangeReason);
        EnsureModifiersReleased();
        var point = InputPoint(); CheckPoint(point.X, point.Y, "draft-read-click"); CheckActivity(version, ct);
        var clickStarted = Stopwatch.GetTimestamp();
        Native.VisionClick(point.X, point.Y); CheckActivity(version, ct);
        var shortcutsStarted = Stopwatch.GetTimestamp();
        try
        {
            Native.VisionChord(0x41);
        }
        finally
        {
            // Copy and collapse immediately; no selection screenshot or caret check.
            if (Native.GetForegroundWindow() == target && Native.IsWindow(target)) Native.VisionCopyAndCollapse();
        }
        var pollStarted = Stopwatch.GetTimestamp();
        var read = await VisionClipboardPoller.ReadAsync(() =>
        {
            var available = clipboard.TryReadCopied(_pid, out var text);
            return (available, text);
        }, () => CheckActivity(version, ct), ct);
        DebugSession.Current?.Event("visual_draft_copy", new { read.ClipboardUpdated,
            copiedCharacters = read.ClipboardUpdated ? (int?)read.Text.Length : null,
            phase = "readback",
            clickMs = Stopwatch.GetElapsedTime(clickStarted, shortcutsStarted).TotalMilliseconds,
            shortcutsMs = Stopwatch.GetElapsedTime(shortcutsStarted, pollStarted).TotalMilliseconds,
            read.Attempts, read.ElapsedMs, maxCopyWaitMs = 150 });
        using var image = Screenshot(ct);
        if (!await CheckTitleAsync(image, ct)) throw new InvalidOperationException(StateChangeReason);
        CheckActivity(version, ct);
        return read;
    }

    public async Task<Capture> CaptureAsync(ConversationSnapshot snapshot, int index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot.NativeState != this || _frozenContext is null) throw new InvalidOperationException("聊天快照不属于本次任务。");
        var context = index == 0 ? await CaptureMessagePageAsync(snapshot, firstPage: true, ct) ??
            _frozenContext.Capture(_frame!, _layout!, index)
            : _scrolledContext ?? throw new InvalidOperationException("未取得上滚后的消息区。");
        var capture = context with { Id = $"vision_context_{index}", RequiresConversationCheck = true,
            LocalTitle = string.IsNullOrWhiteSpace(_layout!.Title) ? null : _layout.Title };
        if (DebugSession.Current is { Enabled: true } debug) debug.WriteBytes("greeting_context_" + index, capture.PngBytes, ".png");
        return capture;
    }
    public async Task<bool> IsUnchangedAsync(ConversationSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot.NativeState != this || _frozenContext is null || _disposed) return false;
        if (_guard is null) return true;
        if (!_guard.Valid) return false;
        if (_monitor?.Changed == true) return Reject("切换到了其他程序，未继续预填。");
        if (Native.GetForegroundWindow() == _companion && _companion != 0) return true;
        _ioDepth++;
        try { return await CheckLiveAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return Reject(e.Message); }
        finally { _ioDepth--; }
    }
    public async Task<bool> WaitForAppendReadyAsync(ConversationSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.NativeState != this || _guard is null) return false;
        // The coordinator checks the live title as soon as the target is back in front.
        var result = await UiaForegroundWaiter.WaitAsync(() => Native.GetForegroundWindow() == target,
            _ =>
            {
                if (_monitor?.Changed == true) return Task.FromResult(Reject("切换到了其他程序，未继续预填。"));
                var foreground = Native.GetForegroundWindow();
                return Task.FromResult(!_disposed && _guard.Valid &&
                    (foreground == target || foreground == _companion && _companion != 0));
            }, () => Report("等待返回微信", "请回到原微信聊天，核对标题后追加到当前草稿", 87), ct);
        return result == ForegroundWaitResult.Ready || Reject(result == ForegroundWaitResult.TimedOut ? "等待返回微信超时，请复制正文。" : StateChangeReason);
    }
    public async Task<bool> ScrollUpAsync(ConversationSnapshot snapshot, CancellationToken ct)
    {
        _scrolledContext = await CaptureMessagePageAsync(snapshot, firstPage: false, ct);
        return _scrolledContext is not null;
    }
    private async Task<Capture?> CaptureMessagePageAsync(ConversationSnapshot snapshot, bool firstPage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot.NativeState != this || _frozenContext is null || !CanCollectMoreContext) return null;
        var monitor = _monitor!;
        Capture? Stop(string reason)
        {
            _contextScrollHalted = true;
            Debug?.Event("visual-context-scroll-stopped", new { reason });
            return null;
        }
        bool CanContinue() => !_disposed && !monitor.Changed && !monitor.WheelObserved && _guard is not { Valid: false };
        var ready = await UiaForegroundWaiter.WaitAsync(() => Native.GetForegroundWindow() == target,
            _ => Task.FromResult(CanContinue() &&
                VisionInteractionState.IsAllowedForeground(Native.GetForegroundWindow(), target, _companion)),
            () => Report("等待返回微信", "请在 10 秒内回到原聊天，继续采集消息", 25), ct,
            timeout: TimeSpan.FromSeconds(10));
        if (ready != ForegroundWaitResult.Ready)
            return Stop(ready == ForegroundWaitResult.TimedOut ? "wechat-foreground-timeout" :
                monitor.WheelObserved ? "user-scrolled" : "foreground-or-conversation-changed");

        var version = monitor.ActivityVersion;
        bool SameActivity() => CanContinue() && Native.GetForegroundWindow() == target && monitor.ActivityVersion == version;
        _ioDepth++;
        try
        {
            using var initial = _frozenContext.CopyImage();
            using var before = Screenshot(ct);
            if (!await CheckTitleAsync(before, ct)) return Stop("title-changed-before-scroll");
            var frame = _frame!;
            var layout = _layout!;
            Task<bool> CheckGeometryAsync(Bitmap image) => Task.Run(() =>
                WechatLocalLayout.ConfirmMessageGeometry(initial, image, frame, layout, _anchors!, ct), ct);
            if (!await CheckGeometryAsync(before)) return Stop("layout-changed-before-scroll");
            if (!SameActivity()) return Stop("activity-before-scroll");

            var messages = frame.ToClientRectangle(layout.Messages!);
            var gutter = (int)Math.Ceiling(23d * frame.Dpi / 96);
            if (messages.Width <= gutter * 2) return Stop("message-pane-too-narrow");
            var content = Rectangle.FromLTRB(messages.Left, messages.Top, messages.Right - gutter, messages.Bottom);
            var previous = firstPage ? null : VisionRegionSnapshot.Capture(before, content);

            if (firstPage)
            {
                var point = VisionMessageScroll.BottomThumbPoint(frame.ToClientRectangle(layout.Input!), frame.Dpi);
                if (!messages.Contains(point) || content.Contains(point)) return Stop("page-up-thumb-outside-scrollbar-gutter");
                var x = frame.Bounds.Left + point.X;
                var y = frame.Bounds.Top + point.Y;
                if (!SameActivity() || !Native.GetCursorPos(out var cursor)) return Stop("activity-before-thumb-click");
                EnsureModifiersReleased();
                if ((Native.GetAsyncKeyState(0x01) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x02) & 0x8000) != 0 ||
                    (Native.GetAsyncKeyState(0x04) & 0x8000) != 0) return Stop("mouse-button-held");
                CheckPoint(x, y, "context-scroll-click");
                try { Native.VisionClick(x, y); }
                finally
                {
                    if (Native.GetCursorPos(out var current) && current.X == x && current.Y == y)
                        Native.SetCursorPos(cursor.X, cursor.Y);
                }
            }
            var key = (ushort)(firstPage ? 0x22 : 0x21);
            var presses = firstPage ? 3 : 1;
            ct.ThrowIfCancellationRequested();
            if (!SameActivity()) return Stop("activity-before-page-key");
            EnsureModifiersReleased();
            if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) return Stop("page-key-already-held");
            Native.VisionPage(key, presses);
            var started = Stopwatch.GetTimestamp();
            var observations = 0;
            while (true)
            {
                if (!SameActivity()) return Stop("activity-after-page-key");
                using var after = Screenshot(ct);
                observations++;
                var next = VisionRegionSnapshot.Capture(after, content);
                var same = previous?.Matches(next, allowCaret: false);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (firstPage ? same == true && elapsed >= 60 : same == false)
                {
                    if (!await CheckTitleAsync(after, ct)) return Stop("title-changed-after-page-key");
                    if (!await CheckGeometryAsync(after)) return Stop("layout-changed-after-page-key");
                    if (!SameActivity()) return Stop("activity-after-page-key");
                    var capture = VisionContextSnapshot.CreateCapture(after, frame, layout, 0);
                    var shift = firstPage ? 0 : VisionMessageScroll.EstimateDownwardShift(previous!, next);
                    Debug?.Event("visual-context-scroll-page", new { key = firstPage ? "PageDown" : "PageUp", presses,
                        measuredPixels = shift > 0 ? (int?)shift : null, observations,
                        elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                    return capture;
                }
                if (firstPage) previous = next;
                var remaining = 220 - elapsed;
                if (remaining <= 0) return Stop(firstPage ? "page-down-messages-not-settled" : "page-up-did-not-change-messages");
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(15, remaining)), ct);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
        {
            Debug?.Event("visual-context-scroll-stopped", new { e.Message });
            _contextScrollHalted = true;
            return null;
        }
        finally { _ioDepth--; }
    }

    public async Task<FillResult> AppendAsync(ConversationSnapshot snapshot, string greeting, CancellationToken ct)
    {
        if (!await _appendGate.WaitAsync(0, ct)) return new("待手动处理", "正在核对预填，未重复写入。");
        _ioDepth++;
        try
        {
            if (snapshot.NativeState != this || !enableAppend || _guard is null || !snapshot.DirectAppend || snapshot.TitleSource != "ocr")
                return new("待手动处理", AutoAppendUnavailableReason);
            if (_guard.WriteIssued) return new("预填结果待确认", "本次已尝试填写，未重复写入。");
            if (!_guard.Valid) return new("待手动处理", StateChangeReason);
            var version = CheckInputReady(ct);
            using (var currentFrame = Screenshot(ct, "before_visual_prefill"))
            using (var initial = _frozenContext!.CopyImage())
            {
                var started = Stopwatch.GetTimestamp();
                var (sameGeometry, reused) = await Task.Run(() =>
                {
                    if (WechatLocalLayout.CanReuseGeometry(initial, currentFrame, _frame!.Dpi, _layout!, _anchors!, ct))
                        return (true, true);
                    var located = WechatLocalLayout.Locate(currentFrame, _frame!.Dpi, ct, _identity);
                    return (WechatLocalLayout.SameGeometry(_layout!, located.Layout, currentFrame.Width, currentFrame.Height), false);
                }, ct);
                Debug?.Event("visual-prefill-layout-checked", new { reused, sameGeometry, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
                if (!sameGeometry) return new("待手动处理", "本地复查发现输入区布局已变化，未填写。");
            }
            if (!await CheckLiveAsync(ct)) return new("待手动处理", StateChangeReason);
            CheckActivity(version, ct);
            var backupStarted = Stopwatch.GetTimestamp();
            VisionClipboard? clipboard = null;
            Native.INPUT[]? directInput = null;
            try
            {
                clipboard = _clipboard = new VisionClipboard(clipboardOwner);
                Debug?.Event("visual-clipboard-backup", new { elapsedMs = Stopwatch.GetElapsedTime(backupStarted).TotalMilliseconds });
            }
            catch (ClipboardBackupUnavailableException)
            {
                directInput = Native.PrepareUnicodeInput(greeting);
            }
            var point = InputPoint(); CheckPoint(point.X, point.Y, "prefill-click"); CheckActivity(version, ct);
            Native.VisionClick(point.X, point.Y);
            EnsureModifiersReleased();
            CheckActivity(version, ct);
            Native.VisionChord(0x23);
            VisionRegionSnapshot beforePaste;
            using (var image = Screenshot(ct))
            {
                if (!await CheckTitleAsync(image, ct)) return new("待手动处理", StateChangeReason);
                beforePaste = CaptureInput(image, true);
            }
            EnsureModifiersReleased();
            CheckActivity(version, ct);
            clipboard?.PutText(greeting);
            CheckActivity(version, ct);
            if (!_guard.TryIssueWrite()) return new("预填结果待确认", "本次写入状态已变化，未重复填写。");
            if (directInput is not null)
            {
                Native.Keys(directInput);
                await WaitForInputSettledAsync(version, ct, beforePaste);
                return new("预填结果待确认", "已通过键盘尝试追加祝福，原剪贴板已保留。请检查微信草稿。");
            }
            Native.VisionChord(0x56);
            DebugSession.Current?.Event("visual_paste_issued", new { characters = greeting.Length, ocrEngine = _ocr.Name,
                readbackWait = "input-changed-and-stable" });
            CheckActivity(version, ct);
            // Keep the paste payload on the clipboard until WeChat replaces it with a fresh copy.
            await WaitForInputSettledAsync(version, ct, beforePaste);
            var actual = await ReadDraftAsync(clipboard!, version, ct);
            return actual.ConfirmsSuffix(greeting)
                ? new("已预填", "OCR 标题一致，已读回文末祝福。请检查后手动发送。")
                : new("预填结果待确认", "未能确认文末祝福，请检查微信；未重复写入。");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Reject(e.Message);
            return new(_guard?.WriteIssued == true ? "预填结果待确认" : "待手动处理", e.Message);
        }
        finally
        {
            try { _clipboard?.Dispose(); }
            catch (Exception e) { CleanupWarning = e.Message; }
            finally { _clipboard = null; _ioDepth--; _appendGate.Release(); }
        }
    }
    public Task RestoreAsync(ConversationSnapshot snapshot) { Dispose(); return Task.CompletedTask; }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _watch?.Stop(); _watch?.Dispose(); _watch = null;
        _monitor?.Dispose(); _monitor = null;
        try { _clipboard?.Dispose(); } catch (Exception e) { CleanupWarning = e.Message; }
        _clipboard = null; _frozenContext?.Dispose(); _frozenContext = null; PreviewPng = null;
    }
    private static byte[] Png(Bitmap image) { using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png); return stream.ToArray(); }
    private static void DebugImage(string stage, Bitmap image)
    { if (DebugSession.Current is { Enabled: true } debug) debug.WriteBytes(stage, Png(image), ".png"); }
    private byte[] Preview(Bitmap bitmap, VisionLayout layout)
    {
        using var preview = (Bitmap)bitmap.Clone(); using var graphics = Graphics.FromImage(preview);
        foreach (var (box, color) in new[] { (layout.Chat!, Color.DodgerBlue), (layout.Header!, Color.DeepSkyBlue),
            (layout.Messages!, Color.MediumSeaGreen), (layout.Input!, Color.MediumPurple), (layout.Editor!, Color.DarkOrange), (layout.Send!, Color.Red) })
        { using var pen = new Pen(color, 3); graphics.DrawRectangle(pen, _frame!.ToClientRectangle(box)); }
        foreach (var box in new[] { _anchors!.TopToolbar, _anchors.BottomToolbar })
        { using var pen = new Pen(Color.Gold, 3); graphics.DrawRectangle(pen, _frame!.ToClientRectangle(box)); }
        return Png(preview);
    }
    private static void EnsureModifiersReleased()
    {
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xE5 }.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0))
            throw new InvalidOperationException("请完成输入法组合并松开修饰键后重试。");
    }
}
