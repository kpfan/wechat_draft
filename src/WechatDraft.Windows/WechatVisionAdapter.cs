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
    private VisionAnchorHints? _anchors;
    private OcrPrefillGuard? _guard;
    private VisionInputRecheck? _inputCheck;
    private DebugSession? _debug;
    private Rectangle? _knownEmptyHint;
    private VisionInputMonitor? _monitor;
    private VisionClipboard? _clipboard;
    private System.Windows.Forms.Timer? _watch;
    private bool _watchBusy, _disposed, _originalRead;
    private int _ioDepth, _lastReadActivityVersion;
    private nint _companion;
    private string _prefillReason = "";
    private readonly SemaphoreSlim _appendGate = new(1, 1);
    private DebugSession? Debug => _debug ?? DebugSession.Current;
    public bool CanAutoAppend => enableAppend && _originalRead && _guard is { Valid: true, PasteIssued: false };
    public string AutoAppendUnavailableReason => _prefillReason.Length > 0 ? _prefillReason : enableAppend
        ? "本地 OCR 或完整草稿未通过核对，请检查后复制正文。" : "预填已关闭。请检查后复制正文。";
    public string StateChangeReason => _guard?.Reason is { Length: > 0 } reason ? reason : _prefillReason;
    public string CleanupWarning { get; private set; } = "";
    public bool CanCollectMoreContext => false;
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
        try { located = await Task.Run(() => WechatLocalLayout.Locate(bitmap, dpi, ct), ct); }
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
        if (!enableAppend) return CreateReadonlySnapshot(ct);
        Native.GetWindowThreadProcessId(companion, out var ownerPid);
        _companion = companion != 0 && ownerPid == Environment.ProcessId ? companion : 0;
        try
        {
            _monitor = new VisionInputMonitor(target, stopOnWheel: false, stopOnClick: false, stopOnKeyboard: false, companion: _companion);
            Report("读取草稿", "本地 OCR 标题已读取，正在核对输入区并保存原文", 18);
            var original = (await ReadDraftAsync(ct)).Text;
            _guard = new(_layout!.Title, original); _originalRead = true;
            var baseline = await WaitForInputSettledAsync(_lastReadActivityVersion, ct);
            _inputCheck = new(_guard, baseline, _frame!.Dpi, Debug);
            _watch = new System.Windows.Forms.Timer { Interval = 400 };
            _watch.Tick += async (_, _) => await ObserveAsync(ct);
            _watch.Start();
            return new(Guid.NewGuid().ToString("N"), _layout.Title, original, _identity, false, this, TitleSource: "ocr");
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException)
        {
            _prefillReason = "本次仅生成，未预填：" + e.Message;
            _monitor?.Dispose(); _monitor = null; _originalRead = false;
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
                CheckPoint(_bounds.Left + 16 + (_bounds.Width - 32) * col / 6, _bounds.Top + 16 + (_bounds.Height - 32) * row / 4);
        var image = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, image.Size);
            EnsureWindow(ct); _frame.ValidateImageSize(image.Size);
            if (debugStage is not null) DebugImage(debugStage, image);
            return image;
        }
        catch { image.Dispose(); throw; }
    }
    private void CheckPoint(int x, int y)
    {
        if (Native.GetAncestor(Native.WindowFromPoint(new Native.Point { X = x, Y = y }), 2) != target)
            throw new InvalidOperationException("微信关键区域被遮挡，未继续预填。");
    }
    private bool Reject(string reason)
    {
        if (_prefillReason.Length == 0) Debug?.Event("visual-prefill-rejected", new { reason });
        _prefillReason = reason; _guard?.Reject(reason); return false;
    }
    private async Task<bool> CheckLiveAsync(CancellationToken ct, bool checkInput, bool recheckDraft = true)
    {
        EnsureWindow(ct);
        if (_guard is { Valid: false }) return false;
        using var current = Screenshot(ct);
        var title = await ReadTitleAsync(current, ct);
        EnsureWindow(ct);
        if (_guard is not null ? !_guard.CheckTitle(title) : title != _layout!.Title) return Reject("OCR 聊天标题已变化，未继续预填。");
        // A timer that was awaiting OCR must not observe our own copy/paste in progress.
        if (!recheckDraft && (_ioDepth > 0 || _guard?.PasteIssued == true)) return _guard?.Valid != false;
        if (checkInput && _inputCheck is not null)
        {
            _inputCheck.Observe(CaptureInput(current));
            if (recheckDraft && _inputCheck.Pending)
            {
                _ioDepth++;
                try
                {
                    return await _inputCheck.ConfirmAsync(ReadDraftAsync,
                        token => WaitForInputSettledAsync(_lastReadActivityVersion, token), ct);
                }
                finally { _ioDepth--; }
            }
        }
        return true;
    }
    private VisionRegionSnapshot CaptureInput(Bitmap image) => VisionRegionSnapshot.CaptureInput(image, _layout!, _frame!.Dpi,
        normalizeEmptyHint: _guard is { OriginalText: "" }, knownEmptyHint: _knownEmptyHint,
        hintObserved: hint => _knownEmptyHint = hint);
    private Task<VisionRegionSnapshot> WaitForInputSettledAsync(int version, CancellationToken ct, VisionRegionSnapshot? changedFrom = null) =>
        VisionInputSettler.WaitAsync(async token =>
        {
            using var image = Screenshot(token);
            var title = await ReadTitleAsync(image, token);
            CheckActivity(version, token);
            if (_guard is null || !_guard.CheckTitle(title)) throw new InvalidOperationException(StateChangeReason);
            return CaptureInput(image);
        }, () => CheckActivity(version, ct), _frame!.Dpi, ct, changedFrom);
    private async Task ObserveAsync(CancellationToken ct)
    {
        if (_disposed || _watchBusy || _ioDepth > 0 || _guard is not { Valid: true, PasteIssued: false } || Native.GetForegroundWindow() != target) return;
        _watchBusy = true;
        try { await CheckLiveAsync(ct, true, recheckDraft: false); }
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
                activity = _monitor?.LastActivity, pasteIssued = _guard?.PasteIssued == true });
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
    private async Task<ClipboardDraftRead> ReadDraftAsync(CancellationToken ct)
    {
        _ioDepth++;
        var owned = _clipboard is null;
        try
        {
            if (!await CheckLiveAsync(ct, false)) throw new InvalidOperationException(StateChangeReason);
            var version = CheckInputReady(ct);
            _clipboard ??= new VisionClipboard(clipboardOwner);
            var point = InputPoint(); CheckPoint(point.X, point.Y); CheckActivity(version, ct);
            Native.VisionClick(point.X, point.Y); CheckActivity(version, ct);
            _clipboard.PutText("WechatDraft-visual-read-" + Guid.NewGuid().ToString("N"));
            try
            {
                Native.VisionChord(0x41);
            }
            finally
            {
                // Copy and collapse immediately; no selection screenshot or caret check.
                if (Native.GetForegroundWindow() == target && Native.IsWindow(target)) Native.VisionCopyAndCollapse();
            }
            var read = await VisionClipboardPoller.ReadAsync(() =>
            {
                var available = _clipboard.TryReadCopied(_pid, out var text);
                return (available, text);
            }, () => CheckActivity(version, ct), ct);
            DebugSession.Current?.Event("visual_draft_copy", new { read.ClipboardUpdated, assumedEmpty = !read.ClipboardUpdated,
                copiedCharacters = read.ClipboardUpdated ? (int?)read.Text.Length : null,
                rule = "unchanged-clipboard-assumed-empty-after-response-window", read.Attempts, read.ElapsedMs, maxCopyWaitMs = 150 });
            if (!await CheckLiveAsync(ct, false)) throw new InvalidOperationException(StateChangeReason);
            CheckActivity(version, ct);
            _lastReadActivityVersion = version;
            return read;
        }
        finally
        {
            if (owned && _clipboard is not null)
            {
                try { _clipboard.Dispose(); }
                finally { _clipboard = null; _ioDepth--; }
            }
            else _ioDepth--;
        }
    }

    public Task<Capture> CaptureAsync(ConversationSnapshot snapshot, int index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot.NativeState != this || _frozenContext is null) throw new InvalidOperationException("聊天快照不属于本次任务。");
        var capture = _frozenContext.Capture(_frame!, _layout!, index) with { RequiresConversationCheck = true,
            LocalTitle = string.IsNullOrWhiteSpace(_layout!.Title) ? null : _layout.Title };
        if (DebugSession.Current is { Enabled: true } debug) debug.WriteBytes("greeting_context_" + index, capture.PngBytes, ".png");
        return Task.FromResult(capture);
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
        try { return await CheckLiveAsync(ct, true); }
        catch (Exception e) when (e is not OperationCanceledException) { return Reject(e.Message); }
        finally { _ioDepth--; }
    }
    public async Task<bool> WaitForAppendReadyAsync(ConversationSnapshot snapshot, CancellationToken ct)
    {
        if (_guard is null) return false;
        var result = await UiaForegroundWaiter.WaitAsync(() => Native.GetForegroundWindow() == target,
            token => IsUnchangedAsync(snapshot, token), () => Report("等待返回微信", "请回到原微信聊天，OCR 标题和原文核对后预填", 87), ct);
        return result == ForegroundWaitResult.Ready || Reject(result == ForegroundWaitResult.TimedOut ? "等待返回微信超时，请复制正文。" : StateChangeReason);
    }
    public Task<bool> ScrollUpAsync(ConversationSnapshot snapshot, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(false); }

    public async Task<FillResult> AppendAsync(ConversationSnapshot snapshot, string greeting, CancellationToken ct)
    {
        if (!await _appendGate.WaitAsync(0, ct)) return new("待手动处理", "正在核对预填，未重复写入。");
        _ioDepth++;
        try
        {
            if (snapshot.NativeState != this || !_originalRead || _guard is null || !snapshot.OriginalTextRead || snapshot.TitleSource != "ocr")
                return new("待手动处理", AutoAppendUnavailableReason);
            if (_guard.PasteIssued) return new("预填结果待确认", "本次已尝试填写，未重复粘贴。");
            if (!_guard.Valid || !await CheckLiveAsync(ct, true)) return new("待手动处理", StateChangeReason);
            using (var currentFrame = Screenshot(ct, "before_visual_prefill"))
            {
                var located = await Task.Run(() => WechatLocalLayout.Locate(currentFrame, _frame!.Dpi, ct), ct);
                if (!WechatLocalLayout.SameGeometry(_layout!, located.Layout, currentFrame.Width, currentFrame.Height))
                    return new("待手动处理", "本地复查发现输入区布局已变化，未填写。");
            }
            _clipboard = new VisionClipboard(clipboardOwner);
            var current = (await ReadDraftAsync(ct)).Text;
            if (!_guard.CheckDraft(current)) return new("待手动处理", _guard.Reason);
            var version = _lastReadActivityVersion;
            CheckActivity(version, ct);
            var beforePaste = await WaitForInputSettledAsync(version, ct);
            EnsureModifiersReleased();
            if (!await CheckLiveAsync(ct, false)) return new("待手动处理", StateChangeReason);
            CheckActivity(version, ct);
            _clipboard.PutText(DraftRules.AppendSuffix(snapshot.OriginalText, greeting));
            Native.VisionChord(0x23);
            CheckActivity(version, ct);
            if (!_guard.TryIssuePaste()) return new("预填结果待确认", "本次写入状态已变化，未重复粘贴。");
            Native.VisionChord(0x56);
            DebugSession.Current?.Event("visual_paste_issued", new { characters = greeting.Length, ocrEngine = _ocr.Name,
                readbackWait = "input-changed-and-stable" });
            CheckActivity(version, ct);
            // Keep the suffix on the clipboard until WeChat has visibly consumed it.
            // A read probe issued earlier could otherwise replace the pending paste.
            await WaitForInputSettledAsync(version, ct, beforePaste);
            var actual = (await ReadDraftAsync(ct)).Text;
            var preserved = DraftRules.NormalizeNewlines(actual) == DraftRules.NormalizeNewlines(DraftRules.Append(snapshot.OriginalText, greeting));
            return preserved ? new("已预填", "OCR 标题一致，原文与新增祝福已读回核对。请检查后手动发送。", true)
                : new("预填结果待确认", "未读回完整预期草稿，请检查微信；未重复写入。");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Reject(e.Message);
            return new(_guard?.PasteIssued == true ? "预填结果待确认" : "待手动处理", e.Message);
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
