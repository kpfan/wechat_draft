using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using WechatDraft.Core;

namespace WechatDraft.Windows;

public sealed record WechatWindow(nint Handle, string Title, string Process, int ProcessId, long ProcessStartTicks = 0)
{
    public override string ToString() => $"{Process} · {Title} · PID {ProcessId}";
}

public sealed class WechatAdapter(nint target, bool enableAppend, nint assistantWindow = 0,
    Action<TaskProgress>? progress = null) : IConversationAdapter, IDisposable
{
    private static readonly SemaphoreSlim InspectionGate = new(1, 1);
    private sealed record State(AutomationElement Root, AutomationElement Editor, AutomationElement Header,
        string HeaderName, string RootName, System.Windows.Rect MessageArea, string ProcessIdentity,
        System.Windows.Rect WindowBounds, AutomationElement? ScrollElement, double ScrollPercent, UiaConversationIdentity? ConversationIdentity,
        string? IdentityUnavailableReason);
    private nint _assistantWindow;
    private string? _failureCode;
    private int _interruptionLogged;
    private DebugSession? _debug;
    private volatile bool _pasteIssued;
    private volatile bool _disposed;
    private VisionInputMonitor? _inputMonitor;
    private UiaTaskChanges? _taskChanges;
    private nint _foregroundHook;
    private Native.WinEventProc? _callback;
    private AutomationEventHandler? _selectionHandler;
    private AutomationEventHandler? _textHandler;
    private AutomationPropertyChangedEventHandler? _editHandler;
    private AutomationPropertyChangedEventHandler? _headerHandler;
    private AutomationPropertyChangedEventHandler? _scrollHandler;
    private volatile bool _programmaticScroll;
    private volatile bool _userScrolled;
    private bool _scrolled;
    private string? _expectedAfterAppend;
    private State? _state;
    public string StateChangeReason => (_failureCode ?? _taskChanges?.Reason ?? _inputMonitor?.Reason) switch
    {
        "foreground-change" => "前台已切换到其他程序，已停止预填。生成内容已保留，可自行复制。",
        "foreground-wait-timeout" => "等待返回微信超过 30 秒，未预填。生成内容已保留，可自行复制。",
        "draft-changed" => "微信草稿在任务期间被修改，已停止预填。生成内容已保留，可自行复制。",
        "chat-changed" => "微信聊天或聊天控件已变化，已停止预填。生成内容已保留，可自行复制。",
        "window-changed" => "微信窗口位置、大小或窗口实例已变化，已停止预填。",
        "window-minimized" => "微信窗口已最小化，已停止预填。生成内容已保留。",
        "snapshot-unavailable" => "无法核对本次会话快照，已停止预填。",
        "adapter-disposed" => "本次会话检查已结束，未预填。",
        "uia-state-unavailable" => "无法重新读取微信会话或草稿，已停止预填。生成内容已保留。",
        _ => ""
    };
    public bool CanAutoAppend => enableAppend && CanVerifyConversation;
    public bool CanVerifyConversation => !_disposed && _state?.ConversationIdentity is not null;
    public string AutoAppendUnavailableReason => !enableAppend ? "预填已关闭。请检查后复制正文。"
        : (_state?.IdentityUnavailableReason ?? "UIA 未提供可复查的会话标识。") + "无法确认同一聊天，已保留祝福但未预填。";

    public static IReadOnlyList<WechatWindow> FindWindows()
    {
        var windows = new List<WechatWindow>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                if (process.ProcessName.Equals("WeChat", StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("Weixin", StringComparison.OrdinalIgnoreCase))
                    windows.Add(new(hwnd, Native.Title(hwnd), process.ProcessName, process.Id, process.StartTime.ToUniversalTime().Ticks));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, 0);
        return windows;
    }

    public static async Task<UiaRegionExport> ExportRegionsAsync(nint hwnd, CancellationToken cancellationToken)
    {
        if (!Native.IsWindow(hwnd) || Native.IsIconic(hwnd) || Native.GetForegroundWindow() != hwnd)
            throw new InvalidOperationException("请将要查看的微信单聊完整显示在前台，再导出 UIA 标注图。");
        var state = await InspectAsync(hwnd, cancellationToken);
        return await Task.Run(() =>
        {
            void EnsureSameWindow()
            {
                cancellationToken.ThrowIfCancellationRequested();
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                using var process = Process.GetProcessById((int)pid);
                if (Native.IsIconic(hwnd) || Native.GetForegroundWindow() != hwnd ||
                    state.Root.Current.BoundingRectangle != state.WindowBounds || state.Header.Current.Name != state.HeaderName ||
                    state.ProcessIdentity != $"{pid}:{process.StartTime.ToUniversalTime().Ticks}:{hwnd}")
                    throw new InvalidOperationException("导图期间窗口或会话发生变化，请重新导出。");
            }
            static Rectangle Pixels(System.Windows.Rect rect) => Rectangle.FromLTRB((int)Math.Floor(rect.Left), (int)Math.Floor(rect.Top),
                (int)Math.Ceiling(rect.Right), (int)Math.Ceiling(rect.Bottom));
            EnsureSameWindow();
            var regions = new UiaRegionMap(Pixels(state.WindowBounds), Pixels(state.Header.Current.BoundingRectangle),
                Pixels(state.Editor.Current.BoundingRectangle), Pixels(state.MessageArea),
                state.ScrollElement is null ? null : Pixels(state.ScrollElement.Current.BoundingRectangle), WindowLayoutRules.WindowDpi(hwnd));
            var desktop = new Rectangle(Native.GetSystemMetrics(76), Native.GetSystemMetrics(77), Native.GetSystemMetrics(78), Native.GetSystemMetrics(79));
            if (!desktop.Contains(regions.Window) || (long)regions.Window.Width * regions.Window.Height > 20000000)
                throw new InvalidOperationException("微信窗口需完整位于屏幕内，且截图不能超过两千万像素。");
            for (var row = 0; row < 5; row++)
                for (var column = 0; column < 7; column++)
                {
                    var point = new Native.Point { X = regions.Window.Left + 16 + (regions.Window.Width - 32) * column / 6,
                        Y = regions.Window.Top + 16 + (regions.Window.Height - 32) * row / 4 };
                    var top = Native.WindowFromPoint(point);
                    if (top == 0 || Native.GetAncestor(top, 2) != hwnd) throw new InvalidOperationException("微信窗口被遮挡，请完整显示后重新导图。");
                }
            using var bitmap = new Bitmap(regions.Window.Width, regions.Window.Height, PixelFormat.Format32bppArgb);
            bitmap.SetResolution(96, 96);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(regions.Window.Location, Point.Empty, bitmap.Size);
            EnsureSameWindow();
            return new UiaRegionExport(regions, UiaRegionPreview.Render(bitmap, regions));
        }, cancellationToken);
    }

    public static async Task<bool> CanIdentifyConversationAsync(nint hwnd, CancellationToken cancellationToken) =>
        (await InspectAsync(hwnd, cancellationToken)).ConversationIdentity is not null;

    public static async Task<(bool Available, string? Reason, string? BindingMethod, bool SelectedSessionAvailable, bool Rechecked)>
        InspectConversationIdentityAsync(nint hwnd, CancellationToken cancellationToken)
    {
        var state = await InspectAsync(hwnd, cancellationToken);
        var identity = state.ConversationIdentity;
        cancellationToken.ThrowIfCancellationRequested();
        var rechecked = identity is not null && SameConversation(state);
        return (identity is not null, state.IdentityUnavailableReason, identity?.BindingMethod,
            !string.IsNullOrWhiteSpace(identity?.SelectedSession), rechecked);
    }

    public async Task<ConversationSnapshot> BeginAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state is not null) throw new InvalidOperationException("此适配器已经采集过会话，请重新启动任务。");
        if (target == 0 || !Native.IsWindow(target) || Native.IsIconic(target))
            throw new InvalidOperationException("请先打开微信单聊，并在窗口列表中选择目标窗口。");
        if (Native.GetForegroundWindow() != target && !Native.SetForegroundWindow(target))
            throw new InvalidOperationException("Windows 未允许聚焦微信，请手动打开目标聊天后按快捷键。");
        await Task.Delay(250, cancellationToken);
        await WaitModifiersAsync(cancellationToken);
        var state = await InspectAsync(target, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _state = state;
        _debug = DebugSession.Current;
        // Only the exact window supplied by this process may be used for browsing during generation.
        Native.GetWindowThreadProcessId(assistantWindow, out var assistantProcess);
        _assistantWindow = assistantWindow != 0 && Native.IsWindow(assistantWindow) && assistantProcess == (uint)Environment.ProcessId
            ? assistantWindow : 0;
        DebugSession.Current?.Event("uia_conversation_identity", new { available = CanVerifyConversation, prefillRequested = enableAppend,
            reason = state.IdentityUnavailableReason, bindingMethod = state.ConversationIdentity?.BindingMethod,
            selectedSessionAvailable = !string.IsNullOrWhiteSpace(state.ConversationIdentity?.SelectedSession) });
        var original = ReadText(state.Editor);
        var changes = new UiaTaskChanges(original, ((ValuePattern)state.Editor.GetCurrentPattern(ValuePattern.Pattern)).Current.Value,
            state.HeaderName, state.ConversationIdentity);
        _taskChanges = changes;
        if (HasUnfinishedInput()) throw new InvalidOperationException("请先完成输入法组合和松开修饰键，再触发任务。");
        if (enableAppend && CanVerifyConversation)
            _inputMonitor = new VisionInputMonitor(target, stopOnWheel: false, stopOnClick: false, stopOnKeyboard: false, companion: _assistantWindow);
        _callback = (_, _, hwnd, _, _, _, _) =>
        {
            if (VisionInteractionState.IsUnexpectedForeground(hwnd, Native.GetForegroundWindow(), target, _assistantWindow))
                changes.Unavailable("foreground-change");
        };
        _foregroundHook = Native.SetWinEventHook(3, 3, 0, _callback, 0, 0, 0);
        if (_foregroundHook == 0) throw new InvalidOperationException("无法监视目标窗口焦点，已停止自动处理。");
        _selectionHandler = (_, _) => ObserveChange(() => changes.ObserveConversation(
            ReadConversationIdentity(state.Root, state.Header, state.Editor, out _)));
        Automation.AddAutomationEventHandler(SelectionItemPattern.ElementSelectedEvent, state.Root, TreeScope.Subtree, _selectionHandler);
        _editHandler = (_, args) =>
        {
            if (!_pasteIssued) ObserveChange(() => changes.ObserveControlValue(
                args.NewValue as string ?? ((ValuePattern)state.Editor.GetCurrentPattern(ValuePattern.Pattern)).Current.Value));
        };
        _headerHandler = (_, args) => ObserveChange(() => changes.ObserveHeader(args.NewValue as string ?? state.Header.Current.Name));
        Automation.AddAutomationPropertyChangedEventHandler(state.Editor, TreeScope.Element, _editHandler, ValuePattern.ValueProperty);
        _textHandler = (_, _) => { if (!_pasteIssued) ObserveChange(() => changes.ObserveDraft(ReadText(state.Editor))); };
        Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, state.Editor, TreeScope.Element, _textHandler);
        Automation.AddAutomationPropertyChangedEventHandler(state.Header, TreeScope.Element, _headerHandler, AutomationElement.NameProperty);
        if (state.ScrollElement is not null)
        {
            _scrollHandler = (_, _) => { if (!_programmaticScroll) _userScrolled = true; };
            Automation.AddAutomationPropertyChangedEventHandler(state.ScrollElement, TreeScope.Element, _scrollHandler, ScrollPattern.VerticalScrollPercentProperty);
        }
        return new(Guid.NewGuid().ToString("N"), state.HeaderName, original, state.ProcessIdentity, state);
    }

    private static async Task<State> InspectAsync(nint hwnd, CancellationToken cancellationToken)
    {
        if (!await InspectionGate.WaitAsync(0, cancellationToken))
            throw new UiAutomationUnavailableException("上一次微信控件读取尚未结束。");
        // A late result may only contain observations; it must never register hooks or issue input.
        var inspection = Task.Run(() =>
        {
            try { return Inspect(hwnd); }
            finally { InspectionGate.Release(); }
        });
        _ = inspection.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await inspection.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken); }
        catch (TimeoutException)
        { throw new UiAutomationUnavailableException("读取微信控件超过 8 秒，已停止等待。"); }
    }

    private static State Inspect(nint hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var bounds = root.Current.BoundingRectangle;
        // UIA 坐标为物理像素；位置与尺寸阈值按 96 DPI 逻辑像素书写，先按窗口 DPI 归一化，
        // 否则高缩放下标题栏等控件会被位置过滤误排除。
        var scale = WindowLayoutRules.WindowDpi(hwnd) / 96d;
        System.Windows.Rect Logical(System.Windows.Rect r) => new(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);
        var logicalBounds = Logical(bounds);
        var rawEditors = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        if (rawEditors.Count == 0)
        {
            var semanticControl = root.FindFirst(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
            throw new UiAutomationUnavailableException(semanticControl is null
                ? "微信当前只提供外层窗口，未提供聊天输入框等可访问控件。已停止，未调用模型；当前环境暂不支持自动预填。"
                : "未找到微信聊天输入框。请先打开目标单聊，点击输入框后重新检查。");
        }
        var allEditors = rawEditors.Cast<AutomationElement>().Where(e =>
        {
            if (e.Current.IsOffscreen || !e.Current.IsEnabled) return false;
            var rect = Logical(e.Current.BoundingRectangle);
            return rect.Width > 180 && rect.Top > logicalBounds.Top + logicalBounds.Height * .48 &&
                rect.Left > logicalBounds.Left + logicalBounds.Width * .20 && e.TryGetCurrentPattern(ValuePattern.Pattern, out _);
        }).ToArray();
        var namedEditors = allEditors.Where(e => e.Current.AutomationId == "chat_input_field").ToArray();
        var editors = namedEditors.Length > 0 ? namedEditors : allEditors;
        if (editors.Length == 0)
            throw new UiAutomationUnavailableException("已读到微信编辑控件，但未找到符合位置与读写条件的聊天输入框。");
        if (editors.Length > 1)
            throw new InvalidOperationException($"检测到 {editors.Length} 个可能的聊天输入框，无法唯一确认目标，已停止自动处理。");
        var editor = editors[0]; var inputRect = editor.Current.BoundingRectangle;
        var excluded = new[] { "微信", "WeChat", "Weixin", "聊天", "搜索", "更多", "聊天信息", "返回", "消息", "视频通话", "语音通话", "最小化", "关闭", "最大化" };
        var logicalInput = Logical(inputRect);
        var textControls = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        // 优先用微信自身的 AutomationId 定位标题叶节点，避免位置阈值随 DPI/窗口布局失效；
        // 仅在缺少该标识时回退到逻辑坐标的位置过滤。
        var namedHeaders = textControls.Cast<AutomationElement>().Where(e => !e.Current.IsOffscreen &&
                e.Current.AutomationId.EndsWith(".current_chat_name_label", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(e.Current.Name) && e.Current.Name.Length <= 60 && !excluded.Contains(e.Current.Name)).ToArray();
        var headers = namedHeaders;
        if (headers.Length == 0)
        {
            headers = textControls.Cast<AutomationElement>().Where(e =>
            {
                if (e.Current.IsOffscreen || string.IsNullOrWhiteSpace(e.Current.Name) ||
                    e.Current.Name.Length > 60 || excluded.Contains(e.Current.Name)) return false;
                var rect = Logical(e.Current.BoundingRectangle);
                return rect.Left >= logicalInput.Left - 30 && rect.Top >= logicalBounds.Top + 20 && rect.Bottom < logicalBounds.Top + 110;
            }).ToArray();
        }
        if (headers.Length == 0) throw new UiAutomationUnavailableException("UIA 未提供可读取的聊天标题。");
        if (headers.Length != 1) throw new InvalidOperationException("无法唯一确认聊天标题，已停止采集。此微信版本需要补充适配。");
        var name = headers[0].Current.Name;
        // Only adapters with an accessible explicit single-chat information affordance qualify.
        var singleChat = root.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, "聊天信息"),
            new PropertyCondition(AutomationElement.NameProperty, "Chat Info")));
        var groupAffordance = root.FindAll(TreeScope.Descendants, new OrCondition(
            new PropertyCondition(AutomationElement.NameProperty, "群聊信息"),
            new PropertyCondition(AutomationElement.NameProperty, "群成员")));
        // 会话类型由标题栏类名、群成员数和入口等结构证据判定；只有真人私聊才继续。
        // 群聊、公众号、服务通知及无法确认的类型一律停止，不回退到无法核实会话类型的视觉路径。
        WechatConversationRules.RequirePrivateChat(CollectConversationEvidence(root, headers[0], name,
            singleChat.Count > 0, groupAffordance.Count > 0, out var titleBarBounds));
        var value = (ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern);
        if (value.Current.IsReadOnly || value.Current.Value.Contains('\uFFFC')) throw new InvalidOperationException("输入框含不支持的对象或不可编辑。");
        Native.GetWindowThreadProcessId(hwnd, out var processId);
        using var process = System.Diagnostics.Process.GetProcessById((int)processId);
        var identity = $"{processId}:{process.StartTime.ToUniversalTime().Ticks}:{hwnd}";
        // captureArea 供 CopyFromScreen 截图，保持物理像素；边距与最小尺寸按缩放换算。
        if (titleBarBounds.IsEmpty || titleBarBounds.Height <= 0)
            throw new UiAutomationUnavailableException("UIA 未提供有效的标题栏边界，无法安全裁剪消息区。");
        var left = Math.Ceiling(Math.Max(bounds.Left, inputRect.Left - 10 * scale));
        var top = Math.Ceiling(Math.Max(titleBarBounds.Bottom, headers[0].Current.BoundingRectangle.Bottom));
        var width = Math.Floor(bounds.Right - 8 * scale) - left;
        var height = Math.Floor(inputRect.Top - 10 * scale) - top;
        if (width / scale < 200 || height / scale < 100) throw new InvalidOperationException("可用消息区域过小，请放大微信窗口。");
        var captureArea = new System.Windows.Rect(left, top, width, height);
        var scrolls = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))
            .Cast<AutomationElement>().Where(e => !e.Current.IsOffscreen && e.Current.BoundingRectangle.Left >= captureArea.Left - 15 * scale &&
                e.Current.BoundingRectangle.Height > 100 * scale && e.Current.BoundingRectangle.Bottom <= inputRect.Top + 10 * scale &&
                ((ScrollPattern)e.GetCurrentPattern(ScrollPattern.Pattern)).Current.VerticallyScrollable).ToArray();
        var scrollElement = scrolls.Length == 1 ? scrolls[0] : null;
        var percent = scrollElement is null ? -1 : ((ScrollPattern)scrollElement.GetCurrentPattern(ScrollPattern.Pattern)).Current.VerticalScrollPercent;
        var conversationIdentity = ReadConversationIdentity(root, headers[0], editor, out var identityUnavailableReason);
        return new(root, editor, headers[0], name, root.Current.Name, captureArea, identity, bounds, scrollElement, percent,
            conversationIdentity, identityUnavailableReason);
    }

    private static WechatConversationEvidence CollectConversationEvidence(AutomationElement root, AutomationElement header,
        string chatName, bool hasSingleChatAffordance, bool hasGroupAffordance, out System.Windows.Rect titleBarBounds)
    {
        // 标题栏容器：从会话名叶节点沿 RawView 向上找 mmui 标题栏；找不到则不猜测父容器，按无法确认处理。
        AutomationElement? titleBar = null;
        var current = header;
        for (var depth = 0; depth < 12; depth++)
        {
            if ((current.Current.ClassName ?? "").EndsWith(WechatConversationRules.TitleBarClassSuffix, StringComparison.Ordinal))
            { titleBar = current; break; }
            var parent = TreeWalker.RawViewWalker.GetParent(current);
            if (parent is null) break;
            current = parent;
        }
        titleBarBounds = titleBar?.Current.BoundingRectangle ?? System.Windows.Rect.Empty;
        var classNames = new List<string>();
        int? memberCount = null;
        if (titleBar is not null)
        {
            void CollectClasses(AutomationElement element)
            {
                classNames.Add(element.Current.ClassName ?? "");
                var child = TreeWalker.RawViewWalker.GetFirstChild(element);
                while (child is not null)
                {
                    CollectClasses(child);
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }
            }
            CollectClasses(titleBar);
            var textCondition = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
            // 群成员数是标题栏中独立的叶文本控件；会话名本身可能叫“(1)”，
            // 必须排除名称控件和包含子文本的容器，不能按文本模式猜测。
            foreach (var text in titleBar.FindAll(TreeScope.Descendants, textCondition).Cast<AutomationElement>())
            {
                var value = text.Current.Name?.Trim() ?? "";
                if (value.Length == 0 || value == chatName.Trim()) continue;
                if (text.Current.AutomationId.EndsWith("current_chat_name_label", StringComparison.Ordinal)) continue;
                if (text.FindFirst(TreeScope.Descendants, textCondition) is not null) continue;
                var match = WechatConversationRules.MemberCountLeaf.Match(value);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var count)) { memberCount = count; break; }
            }
        }
        var isBrandPage = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, WechatConversationRules.BrandPageClass)) is not null;
        // Inspect 到此已唯一定位聊天输入框，因此 HasInput 恒为 true；无输入框视图在此前已被输入框检查拦截。
        return new(chatName, true, titleBar is not null, isBrandPage, string.Join(" ", classNames), memberCount,
            hasGroupAffordance, hasSingleChatAffordance);
    }

    private static UiaConversationIdentity? ReadConversationIdentity(AutomationElement root, AutomationElement header, AutomationElement editor,
        out string? unavailableReason)
    {
        unavailableReason = null;
        try
        {
            var headerKey = RuntimeKey(header); var editorKey = RuntimeKey(editor);
            if (headerKey.Length == 0 || editorKey.Length == 0)
            { unavailableReason = "UIA 标题或输入控件缺少运行期标识。"; return null; }
            var chatView = ReadChatViewKey(root, header, editor);
            string selectedSession = "";
            var lists = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "session_list"))
                .Cast<AutomationElement>().ToArray();
            if (lists.Length == 0)
                lists = root.FindAll(TreeScope.Descendants, new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                        new PropertyCondition(AutomationElement.IsSelectionPatternAvailableProperty, true)))
                    .Cast<AutomationElement>().Where(element => element.Current.AutomationId.EndsWith(".session_list", StringComparison.Ordinal)).ToArray();
            if (lists.Length > 1)
            { unavailableReason = $"UIA 会话列表数量为 {lists.Length}，无法唯一定位。"; return null; }
            if (lists.Length == 1 && !lists[0].Current.IsOffscreen)
            {
                var list = lists[0];
                var selected = list.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern)
                    ? ((SelectionPattern)pattern).Current.GetSelection() : Array.Empty<AutomationElement>();
                if (selected.Length == 0)
                    selected = list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty, true))
                        .Cast<AutomationElement>().Where(element => ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected).ToArray();
                if (selected.Length > 1)
                { unavailableReason = $"UIA 返回了 {selected.Length} 个选中会话项，无法唯一确认。"; return null; }
                if (selected.Length == 1)
                {
                    var listKey = RuntimeKey(list); var sessionKey = RuntimeKey(selected[0]);
                    if (listKey.Length > 0 && sessionKey.Length > 0)
                        selectedSession = listKey + "/" + sessionKey + "/" + selected[0].Current.AutomationId;
                }
            }
            if (chatView.Length == 0 && selectedSession.Length == 0)
            { unavailableReason = "UIA 未提供可绑定的当前聊天面板，也未返回唯一选中会话项。"; return null; }
            return new(headerKey, editorKey, selectedSession, chatView);
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or NotSupportedException or COMException)
        { unavailableReason = "读取 UIA 会话标识失败（" + e.GetType().Name + "）。"; return null; }
    }

    private static string RuntimeKey(AutomationElement element)
    {
        var runtime = element.GetRuntimeId();
        return runtime is { Length: > 0 } ? string.Join(",", runtime) : "";
    }

    private static string ReadChatViewKey(AutomationElement root, AutomationElement header, AutomationElement editor)
    {
        // Bind to the live right-hand page and its per-chat title content, never to visible text alone.
        var pages = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "chat_message_page"))
            .Cast<AutomationElement>().Where(element => !element.Current.IsOffscreen).ToArray();
        if (pages.Length != 1) return "";
        var page = pages[0];
        var contents = page.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "content_view"))
            .Cast<AutomationElement>().Where(element => !element.Current.IsOffscreen).ToArray();
        if (contents.Length != 1 || !IsWithin(header, contents[0]) || !IsWithin(editor, page)) return "";
        var pageKey = RuntimeKey(page); var contentKey = RuntimeKey(contents[0]);
        return pageKey.Length > 0 && contentKey.Length > 0 ? pageKey + "/" + contentKey : "";
    }

    private static bool IsWithin(AutomationElement element, AutomationElement ancestor)
    {
        AutomationElement? current = element;
        for (var depth = 0; depth < 32 && current is not null; depth++)
        {
            if (current.Equals(ancestor)) return true;
            current = TreeWalker.RawViewWalker.GetParent(current);
        }
        return false;
    }

    private static bool SameConversation(State state) => state.ConversationIdentity?.Matches(
        ReadConversationIdentity(state.Root, state.Header, state.Editor, out _)) == true;

    private static string ReadText(AutomationElement editor)
    {
        if (!editor.TryGetCurrentPattern(ValuePattern.Pattern, out var raw)) throw new InvalidOperationException("无法完整读取草稿。");
        var text = ((ValuePattern)raw).Current.Value;
        if (editor.Current.AutomationId == "chat_input_field" && editor.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
        {
            var documentText = ((TextPattern)pattern).DocumentRange.GetText(-1);
            if (documentText.Length > 0)
            {
                if (text.Length > 0 && DraftRules.NormalizeNewlines(text) != DraftRules.NormalizeNewlines(documentText))
                    throw new InvalidOperationException("输入框的两种文本读取结果不一致，已停止操作以保护原文。");
                text = documentText;
            }
        }
        if (text.Contains('\uFFFC')) throw new InvalidOperationException("草稿包含非文本对象。");
        return text;
    }

    public Task<bool> IsUnchangedAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (WasInterrupted()) return false;
            if (!VisionInteractionState.IsAllowedForeground(Native.GetForegroundWindow(), target, _assistantWindow))
                return Stop("foreground-change");
            if (Native.IsIconic(target)) return Stop("window-minimized");
            if (snapshot.NativeState is not State state ||
                !ReferenceEquals(state, _state) || snapshot.WindowIdentity != state.ProcessIdentity || snapshot.DisplayName != state.HeaderName ||
                !snapshot.OriginalTextRead) return Stop("snapshot-unavailable");
            if (state.Header.Current.Name != state.HeaderName || (state.ConversationIdentity is not null && !SameConversation(state)))
                return Stop("chat-changed");
            if (state.Root.Current.Name != state.RootName || state.Root.Current.BoundingRectangle != state.WindowBounds)
                return Stop("window-changed");
            if (state.Header.Current.IsOffscreen || state.Editor.Current.IsOffscreen) return Stop("uia-state-unavailable");
            if (ReadText(state.Editor) != snapshot.OriginalText) return Stop("draft-changed");
            Native.GetWindowThreadProcessId(target, out var pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return snapshot.WindowIdentity == $"{pid}:{process.StartTime.ToUniversalTime().Ticks}:{target}" || Stop("window-changed");
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException or NotSupportedException)
        { return Stop("uia-state-unavailable"); }
    }, cancellationToken);

    public Task<bool> WaitForAppendReadyAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken) =>
        WaitForWechatForegroundAsync(snapshot, true, cancellationToken);

    private async Task<bool> WaitForWechatForegroundAsync(ConversationSnapshot snapshot, bool generated, CancellationToken cancellationToken)
    {
        var waiting = false;
        var result = await UiaForegroundWaiter.WaitAsync(() => Native.GetForegroundWindow() == target,
            ct => IsUnchangedAsync(snapshot, ct), () =>
            {
                waiting = true;
                _debug?.Event("uia_waiting_for_wechat", new { generated, timeoutSeconds = 30 });
                progress?.Invoke(new("等待返回微信", generated
                    ? "祝福已生成。请在 30 秒内返回原微信聊天，通过会话和草稿复查后再预填。"
                    : "请在 30 秒内返回原微信聊天，以采集聊天画面。", generated ? 86 : 15));
            }, cancellationToken);
        if (result == ForegroundWaitResult.TimedOut) return Stop("foreground-wait-timeout");
        if (result != ForegroundWaitResult.Ready) return false;
        if (waiting)
        {
            _debug?.Event("uia_returned_to_wechat", new { generated });
            progress?.Invoke(new(generated ? "核对输入" : "采集上下文", "已返回微信，正在复查原聊天与草稿。", generated ? 86 : 15));
        }
        return true;
    }

    public async Task<Capture> CaptureAsync(ConversationSnapshot snapshot, int index, CancellationToken cancellationToken)
    {
        if (!await WaitForWechatForegroundAsync(snapshot, false, cancellationToken))
            throw new InvalidOperationException(StateChangeReason);
        var state = (State)snapshot.NativeState!;
        return await Task.Run(() =>
        {
            if (Native.GetForegroundWindow() != target) throw new InvalidOperationException("微信已不在前台，未采集画面。请重新触发。");
            var rect = state.MessageArea;
            using var bitmap = new Bitmap((int)rect.Width, (int)rect.Height);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen((int)rect.Left, (int)rect.Top, 0, 0, bitmap.Size);
            if (Native.GetForegroundWindow() != target || WasInterrupted())
                throw new InvalidOperationException("采集期间窗口状态已变化，已丢弃画面。请重新触发。");
            using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
            return new Capture($"capture_{index}", stream.ToArray(), RequiresConversationCheck: true, LocalTitle: state.HeaderName);
        }, cancellationToken);
    }

    public async Task<bool> ScrollUpAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken)
    {
        // User browsing does not invalidate the conversation, but must not be fought by automatic scrolling.
        if (UserScrolled || Native.GetForegroundWindow() != target) return false;
        if (!await IsUnchangedAsync(snapshot, cancellationToken) || snapshot.NativeState is not State state || state.ScrollElement is null) return false;
        var scroll = (ScrollPattern)state.ScrollElement.GetCurrentPattern(ScrollPattern.Pattern);
        var before = scroll.Current.VerticalScrollPercent;
        if (before <= 0 || UserScrolled || Native.GetForegroundWindow() != target) return false;
        _programmaticScroll = true;
        try
        {
            scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeDecrement); _scrolled = true;
            await Task.Delay(220, cancellationToken);
            return !UserScrolled && Native.GetForegroundWindow() == target &&
                Math.Abs(scroll.Current.VerticalScrollPercent - before) > 0.01;
        }
        finally { _programmaticScroll = false; }
    }

    public async Task<FillResult> AppendAsync(ConversationSnapshot snapshot, string greeting, CancellationToken cancellationToken)
    {
        if (!CanAutoAppend) return new("待手动处理", AutoAppendUnavailableReason);
        if (_pasteIssued) return new("预填结果待确认", "已尝试过预填，未重复写入。请检查微信。");
        if (!await IsUnchangedAsync(snapshot, cancellationToken)) return new("待手动处理", StateChangeReason);
        if (Native.GetForegroundWindow() != target) return new("待手动处理", "预填前微信已不在前台，未抢占窗口。生成内容已保留。");
        var state = (State)snapshot.NativeState!;
        if (!await WaitForInputPauseAsync(cancellationToken)) return new("待手动处理", WasInterrupted()
            ? StateChangeReason
            : "当前仍有输入操作或输入法组合，未预填。生成内容已保留。");
        await WaitModifiersAsync(cancellationToken);
        var commitActivityVersion = _inputMonitor?.ActivityVersion;
        if (!await IsUnchangedAsync(snapshot, cancellationToken)) return new("待手动处理", StateChangeReason);
        var focused = AutomationElement.FocusedElement;
        if (!focused.Equals(state.Editor) && focused.Current.ControlType == ControlType.Edit)
            return new("待手动处理", "焦点位于另一个输入框，未抢占输入。生成内容已保留。");
        if (Native.GetForegroundWindow() != target) return new("待手动处理", "聚焦前微信已不在前台，未抢占窗口。生成内容已保留。");
        state.Editor.SetFocus();
        await Task.Delay(80, cancellationToken);
        if (!AutomationElement.FocusedElement.Equals(state.Editor)) return new("待手动处理", "输入焦点不属于目标控件，未预填。");
        if (!await IsUnchangedAsync(snapshot, cancellationToken)) return new("待手动处理", StateChangeReason);
        if (state.Editor.Current.AutomationId == "chat_input_field")
        {
            var completeText = DraftRules.Append(snapshot.OriginalText, greeting);
            // Recheck immediately before the single write, with no await or logging between them.
            if (WasInterrupted() || !SameConversation(state) || ReadText(state.Editor) != snapshot.OriginalText || Native.GetForegroundWindow() != target ||
                HasUnfinishedInput() || !AutomationElement.FocusedElement.Equals(state.Editor) || commitActivityVersion != _inputMonitor?.ActivityVersion)
                return new("待手动处理", "预填前会话或草稿已变化，未输入。");
            cancellationToken.ThrowIfCancellationRequested();
            _pasteIssued = true;
            ((ValuePattern)state.Editor.GetCurrentPattern(ValuePattern.Pattern)).SetValue(completeText);
            return await VerifyAppendAsync(snapshot, state, completeText);
        }
        SafeClipboard? clipboard = null;
        Native.INPUT[]? directInput = null;
        try { clipboard = new SafeClipboard(target, greeting); }
        catch (ClipboardBackupUnavailableException) { directInput = Native.PrepareUnicodeInput(greeting); }
        using var clipboardProtection = clipboard;
        // Ctrl+End collapses any selection at the end; no Enter key is ever injected.
        Native.VisionChord(0x23);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await IsUnchangedAsync(snapshot, cancellationToken) || !SameConversation(state) || HasUnfinishedInput() || ModifiersHeld() ||
            Native.GetForegroundWindow() != target ||
            !AutomationElement.FocusedElement.Equals(state.Editor) || commitActivityVersion != _inputMonitor?.ActivityVersion)
            return new("待手动处理", "填写前会话、草稿或输入焦点发生变化，未输入。");
        cancellationToken.ThrowIfCancellationRequested();
        _pasteIssued = true;
        if (directInput is not null) Native.Keys(directInput);
        else Native.VisionChord(0x56);
        return await VerifyAppendAsync(snapshot, state, DraftRules.Append(snapshot.OriginalText, greeting));
    }

    private async Task<FillResult> VerifyAppendAsync(ConversationSnapshot snapshot, State state, string completeText)
    {
        var expected = DraftRules.NormalizeNewlines(completeText);
        _expectedAfterAppend = expected;
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(100); // Input is never replayed or rolled back after this point.
            if (WasInterrupted() || Native.GetForegroundWindow() != target || state.Header.Current.Name != snapshot.DisplayName || !SameConversation(state))
                return new("预填结果待确认", "写入后会话发生变化，请检查微信。");
            if (DraftRules.NormalizeNewlines(ReadText(state.Editor)) == expected)
                return new("已预填", "原文完整保留，草稿已核对。请检查后手动发送。", true);
        }
        return new("预填结果待确认", "未能读回完整预期内容，未重复写入。请检查微信。");
    }

    public async Task RestoreAsync(ConversationSnapshot snapshot)
    {
        try
        {
            if (_scrolled && !UserScrolled && !WasInterrupted() && Native.GetForegroundWindow() == target && snapshot.NativeState is State state &&
                state.ScrollElement is not null && state.Header.Current.Name == state.HeaderName && SameConversation(state) && state.ScrollPercent >= 0 &&
                DraftRules.NormalizeNewlines(ReadText(state.Editor)) == (_expectedAfterAppend ?? DraftRules.NormalizeNewlines(snapshot.OriginalText)))
            {
                _programmaticScroll = true;
                ((ScrollPattern)state.ScrollElement.GetCurrentPattern(ScrollPattern.Pattern)).SetScrollPercent(ScrollPattern.NoScroll, state.ScrollPercent);
                await Task.Delay(100);
            }
        }
        finally { _programmaticScroll = false; Dispose(); }
    }
    private static bool HasUnfinishedInput() => (Native.GetAsyncKeyState(0xE5) & 0x8000) != 0;
    private bool UserScrolled => _userScrolled || _inputMonitor?.WheelObserved == true;
    private void ObserveChange(Action observe)
    {
        try { observe(); }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException or NotSupportedException)
        { _taskChanges?.Unavailable(); }
    }

    private async Task<bool> WaitForInputPauseAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var version = _inputMonitor?.ActivityVersion;
            await Task.Delay(250, cancellationToken);
            if (WasInterrupted()) return false;
            if (version == _inputMonitor?.ActivityVersion && !HasUnfinishedInput()) return true;
        }
        return false;
    }

    private bool WasInterrupted()
    {
        var reason = _taskChanges?.Reason ?? _inputMonitor?.Reason ?? (_disposed ? "adapter-disposed" : null);
        if (reason is null) return false;
        Interlocked.CompareExchange(ref _failureCode, reason, null);
        if (Interlocked.Exchange(ref _interruptionLogged, 1) == 0)
            _debug?.Event("uia_prefill_interrupted", new { reason = _failureCode });
        return true;
    }

    private bool Stop(string reason)
    {
        _taskChanges?.Unavailable(reason);
        Interlocked.CompareExchange(ref _failureCode, _taskChanges?.Reason ?? reason, null);
        WasInterrupted();
        return false;
    }
    private static bool ModifiersHeld() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (Native.GetAsyncKeyState(k) & 0x8000) != 0);
    private static async Task WaitModifiersAsync(CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            if (!ModifiersHeld()) return;
            await Task.Delay(50, ct);
        }
        throw new InvalidOperationException("请松开 Ctrl、Alt、Shift 等修饰键后重试。");
    }
    public void Dispose()
    {
        _disposed = true;
        _inputMonitor?.Dispose();
        _inputMonitor = null;
        if (_foregroundHook != 0) { Native.UnhookWinEvent(_foregroundHook); _foregroundHook = 0; }
        if (_selectionHandler is not null && _state is not null)
        { try { Automation.RemoveAutomationEventHandler(SelectionItemPattern.ElementSelectedEvent, _state.Root, _selectionHandler); } catch (ElementNotAvailableException) { } _selectionHandler = null; }
        if (_editHandler is not null && _state is not null)
        { try { Automation.RemoveAutomationPropertyChangedEventHandler(_state.Editor, _editHandler); } catch (ElementNotAvailableException) { } _editHandler = null; }
        if (_textHandler is not null && _state is not null)
        { try { Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, _state.Editor, _textHandler); } catch (ElementNotAvailableException) { } _textHandler = null; }
        if (_headerHandler is not null && _state is not null)
        { try { Automation.RemoveAutomationPropertyChangedEventHandler(_state.Header, _headerHandler); } catch (ElementNotAvailableException) { } _headerHandler = null; }
        if (_scrollHandler is not null && _state?.ScrollElement is not null)
        { try { Automation.RemoveAutomationPropertyChangedEventHandler(_state.ScrollElement, _scrollHandler); } catch (ElementNotAvailableException) { } _scrollHandler = null; }
    }
}
