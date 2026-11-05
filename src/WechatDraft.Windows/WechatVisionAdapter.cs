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

public sealed class WechatVisionAdapter(nint target, nint clipboardOwner, bool enableAppend, AppSettings settings,
    IWindowVisionModel model, ISecretProtector protector, Action<TaskProgress>? progress = null) : IConversationAdapter, IDisposable
{
    private Native.Rect _bounds;
    private VisionWindowFrame? _frame;
    private string _identity = "";
    private uint _pid;
    private VisionLayout? _layout;
    private VisionInputMonitor? _monitor;
    private VisionClipboard? _clipboard;
    private VisionContextSnapshot? _frozenContext;
    private VisionRegionSnapshot? _headerPixels, _inputPixels;
    private bool _pasteIssued;
    private int _scrolls;
    private bool _retryUsed;
    private uint _dpi;
    private VisionAnchorHints? _anchors;
    public bool CanAutoAppend => enableAppend;
    public string AutoAppendUnavailableReason => settings.EnableLiveAppend && !enableAppend
        ? "本次使用视觉方式生成，无法取得可复查的 UIA 会话标识，未自动预填。请检查后复制正文。" : "预填已关闭。请检查后复制正文。";
    public bool CanCollectMoreContext => enableAppend;
    public string AutomationMethod => "vision";
    public string CleanupWarning { get; private set; } = "";
    public int LayoutApiCallCount { get; private set; }
    public byte[]? PreviewPng { get; private set; }

    internal (VisionWindowFrame Frame, VisionLayout Layout, Bitmap Image, string Identity) CopyObservationBaseline()
    {
        if (enableAppend || _frame is null || _layout is null || _frozenContext is null)
            throw new InvalidOperationException("只读聊天画面尚未准备。");
        return (_frame, _layout, _frozenContext.CopyImage(), _identity);
    }

    private void Report(string stage, string detail, int percent)
    {
        DebugSession.Current?.Event("vision_progress", new { stage, detail, percent });
        progress?.Invoke(new(stage, detail, percent));
    }

    public async Task<VisionLayout> CheckLayoutAsync(CancellationToken ct)
    {
        CheckAuthorization();
        if (!Native.IsWindow(target) || Native.IsIconic(target)) throw new InvalidOperationException("请打开并显示目标微信聊天窗口。");
        if (Native.GetForegroundWindow() != target && !Native.SetForegroundWindow(target)) throw new InvalidOperationException("无法聚焦微信，请打开目标聊天后重新触发。");
        await Task.Delay(250, ct);
        await WaitModifiersAsync(ct);
        if (!TryBounds(out _bounds) || _bounds.Width < 400 || _bounds.Height < 350 ||
            (long)_bounds.Width * _bounds.Height > 20000000)
            throw new InvalidOperationException("微信窗口尺寸不适合自动视觉定位。");
        Native.GetWindowThreadProcessId(target, out _pid);
        _identity = Identity();
        _dpi = Native.GetDpiForWindow(target);
        if (_dpi == 0) throw new InvalidOperationException("无法确认所选微信窗口的显示缩放，已停止定位。");
        _frame = new(new Rectangle(_bounds.Left, _bounds.Top, _bounds.Width, _bounds.Height), _dpi);
        DebugSession.Current?.WriteJson("vision_window", new
        {
            bounds = new { _bounds.Left, _bounds.Top, _bounds.Right, _bounds.Bottom, _bounds.Width, _bounds.Height },
            dpi = _dpi, processId = _pid, autoAppend = enableAppend,
            coordinateSpace = "selected-window-client-physical-pixels", normalizedRange = "0-1000"
        });
        if (enableAppend) _monitor = new VisionInputMonitor(target);
        Report("视觉定位", "正在识别微信窗口布局、聊天标题和输入区域", 10);
        using var bitmap = Screenshot(ct, "initial_window");
        if (!enableAppend)
        {
            _frozenContext = new VisionContextSnapshot(bitmap);
            Report("已固定聊天画面", "现在可以移动鼠标或切换窗口；本次只生成，不操作微信", 11);
            DebugSession.Current?.Event("readonly_frame_frozen", new { liveMonitoring = false, historyScroll = false });
        }
        try { _layout = await LocateAsync(bitmap, false, ct); }
        catch (Exception e) when (!_retryUsed && e is System.Text.Json.JsonException or TransientApiException)
        {
            _retryUsed = true;
            DebugSession.Current?.Event("layout_retry", new { exceptionType = e.GetType().Name, e.Message });
            if (e is TransientApiException transient) await Task.Delay(transient.RetryAfter, ct);
            using var retry = _frozenContext?.CopyImage() ?? Screenshot(ct, "layout_retry_window");
            _layout = await LocateAsync(retry, false, ct);
            _headerPixels = VisionRegionSnapshot.Capture(retry, ToRect(_layout.Header!));
            _inputPixels = VisionRegionSnapshot.CaptureInput(retry, _layout);
            PreviewPng = Preview(retry, _layout);
            return _layout;
        }
        EnsureAfterAnalysis(ct);
        _headerPixels = VisionRegionSnapshot.Capture(bitmap, ToRect(_layout.Header!));
        _inputPixels = VisionRegionSnapshot.CaptureInput(bitmap, _layout);
        PreviewPng = Preview(bitmap, _layout);
        return _layout;
    }

    public async Task<ConversationSnapshot> BeginAsync(CancellationToken ct)
    {
        await CheckLayoutAsync(ct);
        if (!enableAppend)
        {
            Report("采集上下文", "使用启动时的聊天画面生成；可以移动鼠标或切换窗口", 18);
            DebugSession.Current?.Event("screenshot_only_snapshot", new { originalTextRead = false, clipboardUsed = false });
            return new(Guid.NewGuid().ToString("N"), _layout!.Title.Trim(), "", _identity, false, this, OriginalTextRead: false);
        }
        DebugSession.Current?.Event("clipboard_backup_started");
        try { _clipboard = new VisionClipboard(clipboardOwner); }
        catch (Exception e)
        {
            DebugSession.Current?.Event("clipboard_backup_failed", new { exceptionType = e.GetType().Name, e.Message });
            throw;
        }
        DebugSession.Current?.Event("clipboard_backup_completed");
        Report("读取草稿", "已切换纯视觉；只复制核对当前输入区，不删除原文", 18);
        var original = await ReadDraftAsync(ct);
        using (var bitmap = Screenshot(ct, "draft_baseline_window")) _inputPixels = VisionRegionSnapshot.CaptureInput(bitmap, _layout!);
        return new(Guid.NewGuid().ToString("N"), _layout!.Title.Trim(), original, _identity, false, this);
    }

    private void CheckAuthorization()
    {
        var origin = DraftRules.Origin(settings);
        if (settings.AuthorizedOrigin != origin || settings.VisionAuthorizedOrigin != origin)
            throw new InvalidDataException("视觉兜底需要在设置中授权完整微信窗口截图：首次定位可能包含联系人列表和已有草稿。未上传截图、未操作草稿。");
    }

    private string Identity()
    {
        Native.GetWindowThreadProcessId(target, out var pid);
        using var process = Process.GetProcessById((int)pid);
        return $"{pid}:{process.StartTime.ToUniversalTime().Ticks}:{target}";
    }

    private bool TryBounds(out Native.Rect bounds)
    {
        bounds = default;
        var origin = new Native.Point();
        if (!Native.GetClientRect(target, out var client) || !Native.ClientToScreen(target, ref origin)) return false;
        bounds = new Native.Rect { Left = origin.X, Top = origin.Y, Right = origin.X + client.Width, Bottom = origin.Y + client.Height };
        return true;
    }

    private void EnsureWindow(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_monitor?.Changed == true)
        {
            DebugSession.Current?.Event("input_interrupted", new { reason = _monitor.Reason });
            throw new InvalidOperationException(_monitor.Description + "，已停止自动填入。关闭自动追加后，可只生成祝福。");
        }
        if (!Native.IsWindow(target) || Native.IsIconic(target) || Native.GetForegroundWindow() != target || Identity() != _identity ||
            !TryBounds(out var current))
            throw new InvalidOperationException("微信窗口发生变化，已停止；重新触发后会自动定位。");
        var currentDpi = Native.GetDpiForWindow(target);
        if (_frame is null || !_frame.Matches(new Rectangle(current.Left, current.Top, current.Width, current.Height), currentDpi))
            throw new InvalidOperationException("微信窗口位置、尺寸或显示缩放发生变化，已停止；重新触发后会自动定位。");
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xE5 }.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0))
            throw new InvalidOperationException("检测到修饰键或输入法组合状态，已停止操作。");
    }

    private void EnsureAfterAnalysis(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (enableAppend) EnsureWindow(ct);
    }

    private Bitmap Screenshot(CancellationToken ct, string? debugStage = "window_frame")
    {
        EnsureWindow(ct);
        var desktop = new Rectangle(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
        if (_frame is null || !desktop.Contains(_frame.Bounds))
            throw new InvalidOperationException("微信窗口部分位于屏幕之外，请完整显示后重试。");
        // Conservative local occlusion checks supplement the model's in-window dialog detection.
        for (var row = 0; row < 5; row++)
            for (var column = 0; column < 7; column++)
                CheckPoint(_bounds.Left + 16 + (_bounds.Width - 32) * column / 6, _bounds.Top + 16 + (_bounds.Height - 32) * row / 4);
        var image = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using (var graphics = Graphics.FromImage(image))
                graphics.CopyFromScreen(_bounds.Left, _bounds.Top, 0, 0, image.Size);
            EnsureWindow(ct);
            _frame.ValidateImageSize(image.Size);
            if (debugStage is not null) DebugImage(debugStage, image);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    private void CheckPoint(int x, int y)
    {
        var window = Native.WindowFromPoint(new Native.Point { X = x, Y = y });
        if (window == 0 || Native.GetAncestor(window, 2) != target)
            throw new InvalidOperationException("微信关键区域被其他窗口遮挡，已停止视觉操作。");
    }

    private async Task<VisionLayout> LocateAsync(Bitmap bitmap, bool selection, CancellationToken ct)
    {
        try
        {
            if (_frame is null) throw new InvalidOperationException("微信窗口截图范围尚未确认。");
            _frame.ValidateImageSize(bitmap.Size);
            var anchors = await Task.Run(() => WechatTemplateLocator.Locate(bitmap, _dpi, ct), ct);
            DebugSession.Current?.WriteJson("template_match", new { selection, dpi = _dpi, matched = anchors is not null, anchors });
            EnsureAfterAnalysis(ct);
            if (_layout?.InputToolbar is not null && anchors is null)
                throw new InvalidOperationException("本地工具栏模板复核未通过，已停止操作。");
            _anchors = anchors;
            if (_layout is null) Report("视觉定位", anchors is null ? "本地图标未可靠匹配，继续使用视觉模型识别" : "已匹配本地工具栏，正在核对聊天与输入区域", 12);
            if (LayoutApiCallCount >= 6) throw new InvalidOperationException("已达到本次视觉定位与复核请求上限，未重复写入。");
            LayoutApiCallCount++;
            var layout = await model.LocateAsync(settings, protector.Unprotect(settings.ProtectedApiKey), new("window_layout", Png(bitmap)), selection, ct, _layout, anchors);
            DebugSession.Current?.WriteJson("model_layout", new { selection, layout, layout.InputToolbar });
            if (anchors is not null) layout = VisionRules.ApplyAnchors(layout, anchors);
            VisionRules.Validate(layout);
            DebugLayout(bitmap, layout, selection);
            EnsureAfterAnalysis(ct);
            return layout;
        }
        catch (Exception e)
        {
            DebugSession.Current?.Event("vision_layout_failed", new { selection, layoutCall = LayoutApiCallCount, exceptionType = e.GetType().Name, e.Message });
            throw;
        }
    }

    private void CheckHeader(Bitmap bitmap)
    {
        if (_layout is null || _headerPixels is null || !_headerPixels.Matches(VisionRegionSnapshot.Capture(bitmap, ToRect(_layout.Header!)), false))
        {
            DebugSession.Current?.Event("header_changed");
            throw new InvalidOperationException("聊天标题画面发生变化，已停止视觉任务。");
        }
    }

    private async Task<string> ReadDraftAsync(CancellationToken ct)
    {
        if (_layout is null || _clipboard is null) throw new InvalidOperationException("视觉草稿读取尚未准备。");
        using (var before = Screenshot(ct, "before_draft_read"))
        {
            CheckHeader(before);
            var input = VisionRegionSnapshot.CaptureInput(before, _layout);
            if (!_pasteIssued && (_inputPixels is null || !_inputPixels.Matches(input, true)))
            {
                DebugSession.Current?.Event("input_changed_before_read");
                throw new InvalidOperationException("读取前输入区画面变化，已停止操作。");
            }
            if (_pasteIssued)
            {
                // Text has changed by our paste, so re-localize before any verification click.
                var afterPaste = await LocateAsync(before, false, ct);
                if (!VisionRules.SameTarget(_layout, afterPaste)) throw new InvalidOperationException("追加后布局或标题变化，未继续操作。");
                using var latest = Screenshot(ct, "after_paste_stability");
                CheckHeader(latest);
                if (!input.Matches(VisionRegionSnapshot.CaptureInput(latest, _layout), true))
                {
                    DebugSession.Current?.Event("input_changed_after_paste");
                    throw new InvalidOperationException("追加后输入区域仍在变化，未继续操作。");
                }
            }
        }
        var point = VisionRules.InputPoint(_layout);
        var screenPoint = _frame!.ToScreenPoint(point.X, point.Y);
        var x = screenPoint.X; var y = screenPoint.Y;
        CheckPoint(x, y);
        EnsureWindow(ct);
        Native.VisionClick(x, y);
        DebugSession.Current?.Event("focus_click", new { normalized = new { x = point.X, y = point.Y }, screen = new { x, y } });
        await Task.Delay(100, ct);
        EnsureWindow(ct);
        Native.VisionChord(0x41);
        await Task.Delay(90, ct);
        EnsureWindow(ct);
        // Capture the selection locally, then collapse it before waiting for clipboard/model I/O.
        // The user must not be left with their whole draft selected during a network request.
        using var selected = Screenshot(ct, null);
        CheckHeader(selected);
        _clipboard.PutText("WechatDraft-copy-probe-" + Guid.NewGuid().ToString("N"));
        Native.VisionChord(0x43);
        Native.VisionChord(0x23);
        // Keep disk I/O after collapsing the selection, just like model/clipboard waits.
        DebugImage("draft_selection", selected);
        string? copied = null;
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(60, ct);
            EnsureWindow(ct);
            if (_clipboard.TryReadCopied(_pid, out copied)) break;
        }
        DebugSession.Current?.Event("draft_copy_result", new { copied = copied is not null, characters = copied?.Length, expectedSourceProcess = _pid, _pasteIssued });
        var check = await LocateAsync(selected, true, ct);
        if (!VisionRules.SameTarget(_layout, check)) throw new InvalidOperationException("视觉复核发现标题或输入区域变化，已停止操作。");
        return VisionRules.ReadDraft(copied, check);
    }

    public Task<bool> IsUnchangedAsync(ConversationSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!enableAppend) return Task.FromResult(snapshot.NativeState == this && _frozenContext is not null);
        try
        {
            if (snapshot.NativeState != this || _inputPixels is null) return Task.FromResult(false);
            using var bitmap = Screenshot(ct, "state_check");
            CheckHeader(bitmap);
            var unchanged = _inputPixels.Matches(VisionRegionSnapshot.CaptureInput(bitmap, _layout!), allowCaret: true);
            DebugSession.Current?.Event("input_unchanged_check", new { unchanged });
            return Task.FromResult(unchanged);
        }
        catch (InvalidOperationException e)
        {
            DebugSession.Current?.Event("state_check_failed", new { exceptionType = e.GetType().Name, e.Message });
            return Task.FromResult(false);
        }
    }

    public Task<Capture> CaptureAsync(ConversationSnapshot snapshot, int index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot.NativeState != this) throw new InvalidOperationException("聊天快照不属于本次任务。");
        Capture capture;
        if (!enableAppend)
        {
            capture = (_frozenContext ?? throw new InvalidOperationException("启动时的聊天画面尚未准备。")).Capture(_frame!, _layout!, index);
        }
        else
        {
            using var bitmap = Screenshot(ct, "context_source_" + index);
            CheckHeader(bitmap);
            capture = VisionContextSnapshot.CreateCapture(bitmap, _frame!, _layout!, index);
        }
        if (DebugSession.Current is { Enabled: true } debug) debug.WriteBytes("greeting_context_" + index, capture.PngBytes, ".png");
        return Task.FromResult(capture);
    }

    public async Task<bool> ScrollUpAsync(ConversationSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!enableAppend || _scrolls >= 3 || !await IsUnchangedAsync(snapshot, ct)) return false;
        var area = ToRect(_layout!.Messages!);
        using var before = Screenshot(ct, "before_scroll");
        var fingerprint = VisionRegionSnapshot.Capture(before, area);
        var point = VisionRules.ScrollPoint(_layout!, _bounds.Width, _dpi);
        var screenPoint = _frame!.ToScreenPoint(point.X, point.Y);
        var x = screenPoint.X; var y = screenPoint.Y;
        CheckPoint(x, y); EnsureWindow(ct);
        if (!Native.SetCursorPos(x, y)) throw new InvalidOperationException("无法定位消息区。");
        Native.VisionWheel(360); _scrolls++;
        DebugSession.Current?.Event("history_scroll", new { normalized = new { x = point.X, y = point.Y }, screen = new { x, y }, attempt = _scrolls });
        await Task.Delay(400, ct);
        using var after = Screenshot(ct, "after_scroll");
        CheckHeader(after);
        var changed = !fingerprint.Matches(VisionRegionSnapshot.Capture(after, area), false);
        DebugSession.Current?.Event("history_scroll_result", new { changed, scrolls = _scrolls });
        return changed;
    }

    public async Task<FillResult> AppendAsync(ConversationSnapshot snapshot, string greeting, CancellationToken ct)
    {
        if (!enableAppend) return new("待手动处理", "自动追加未启用。");
        if (!snapshot.OriginalTextRead) return new("待手动处理", "本次未读取完整草稿，不能自动追加。");
        if (_pasteIssued) return new("预填结果待确认", "已经尝试过写入，未重复粘贴。");
        if (!await IsUnchangedAsync(snapshot, ct)) return new("待手动处理", "目标或草稿画面变化，未输入。");
        DebugSession.Current?.Event("append_started", new { snapshot.OriginalTextRead, originalCharacters = snapshot.OriginalText.Length, greetingCharacters = greeting.Length });
        Report("视觉核对", "重新复制完整原文，核对标题与输入区域", 88);
        var result = await VisionDraftAppender.AppendAsync(snapshot.OriginalText, greeting, ReadDraftAsync, async (suffix, token) =>
        {
            EnsureWindow(token);
            _clipboard!.PutText(suffix);
            Native.VisionChord(0x23);
            EnsureWindow(token);
            _pasteIssued = true; // Never replay or undo after this point, including cancellation.
            Native.VisionChord(0x56);
            DebugSession.Current?.Event("paste_issued", new { suffixCharacters = suffix.Length });
            await Task.Delay(250, token);
            Report("核对追加", "读回完整草稿；不确定时不重复写入", 95);
        }, ct);
        if (result.OriginalPreserved == true)
        {
            EnsureWindow(ct);
            Native.VisionChord(0x23);
            if (_scrolls > 0) result = result with { Message = result.Message + "消息区保留在补充截图的位置。" };
        }
        DebugSession.Current?.Event("append_verified", new { result.Status, result.Message, result.OriginalPreserved, _pasteIssued });
        return result;
    }

    public Task RestoreAsync(ConversationSnapshot snapshot) { Dispose(); return Task.CompletedTask; }
    public void Dispose()
    {
        _monitor?.Dispose(); _monitor = null;
        try { _clipboard?.Dispose(); }
        catch (InvalidOperationException e) { CleanupWarning = e.Message; }
        finally
        {
            _clipboard = null; _headerPixels = _inputPixels = null;
            _frozenContext?.Dispose(); _frozenContext = null;
            DebugSession.Current?.Event("vision_disposed", new { _pasteIssued, CleanupWarning });
        }
    }

    private Rectangle ToRect(VisionBox box) =>
        (_frame ?? throw new InvalidOperationException("微信窗口截图范围尚未确认。")).ToClientRectangle(box);
    private static byte[] Png(Bitmap image) { using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png); return stream.ToArray(); }
    private static void DebugImage(string kind, Bitmap bitmap)
    {
        if (DebugSession.Current is not { Enabled: true } debug) return;
        try { debug.WriteBytes(kind, Png(bitmap), ".png"); }
        catch (Exception e) { debug.Event("debug_image_failed", new { kind, exceptionType = e.GetType().Name }); }
    }

    private void DebugLayout(Bitmap bitmap, VisionLayout layout, bool selection)
    {
        if (DebugSession.Current is not { Enabled: true } debug) return;
        try
        {
            var clickRegion = VisionRules.ClickRegion(layout);
            var point = VisionRules.InputPoint(layout);
            debug.WriteJson("validated_layout", new
            {
                selection, layout, layout.InputToolbar, clickRegion,
                window = _frame!.Bounds, dpi = _frame.Dpi, screenClickPoint = _frame.ToScreenPoint(point.X, point.Y),
                clickPoint = new { x = point.X, y = point.Y },
                pixels = new { chat = ToRect(layout.Chat!), header = ToRect(layout.Header!), messages = ToRect(layout.Messages!),
                    input = ToRect(layout.Input!), editor = ToRect(layout.Editor!), send = ToRect(layout.Send!), click = ToRect(clickRegion) }
            });
            debug.WriteBytes("layout_preview", Preview(bitmap, layout), ".png");
        }
        catch (Exception e) { debug.Event("debug_layout_failed", new { exceptionType = e.GetType().Name }); }
    }
    private byte[] Preview(Bitmap bitmap, VisionLayout layout)
    {
        using var preview = (Bitmap)bitmap.Clone();
        using var graphics = Graphics.FromImage(preview);
        foreach (var (box, color) in new[] { (layout.Chat!, Color.DodgerBlue), (layout.Header!, Color.DeepSkyBlue),
            (layout.Messages!, Color.MediumSeaGreen), (layout.Input!, Color.MediumPurple),
            (VisionRules.ClickRegion(layout), Color.DarkOrange), (layout.Send!, Color.Red) })
        { using var pen = new Pen(color, 3); graphics.DrawRectangle(pen, ToRect(box)); }
        if (_anchors is not null)
            foreach (var box in new[] { _anchors.TopToolbar, _anchors.BottomToolbar })
            { using var pen = new Pen(Color.Gold, 3); graphics.DrawRectangle(pen, ToRect(box)); }
        var point = VisionRules.InputPoint(layout);
        var local = _frame!.ToClientPoint(point.X, point.Y);
        var x = local.X; var y = local.Y;
        using var marker = new Pen(Color.DarkOrange, 3);
        graphics.DrawLine(marker, x - 6, y, x + 6, y); graphics.DrawLine(marker, x, y - 6, x, y + 6);
        return Png(preview);
    }
    private static async Task WaitModifiersAsync(CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xE5 }.All(k => (Native.GetAsyncKeyState(k) & 0x8000) == 0)) return;
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException("请完成输入法组合并松开修饰键后重试。");
    }
}
