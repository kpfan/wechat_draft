using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WechatDraft.Core;
using WechatDraft.Windows;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace WechatDraft.App;

public sealed partial class MainWindow : Window
{
    private readonly DpapiProtector _protector = new();
    private readonly LocalRepository _repository;
    private readonly string _dataDirectory;
    private readonly ExampleService _examples = new();
    private readonly CompatibleModelClient _client = new();
    private readonly TaskCoordinator _coordinator;
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private HistoryPageView? _historyView;
    private readonly TextBlock _footer = Ui.Text("", 12, "71817B");
    private readonly List<Button> _navigation = [];
    private AppSettings _settings;
    private GlobalHotkey? _hotkey;
    private Action<HotkeyShortcut?>? _finishHotkeyRecording;
    private CancellationTokenSource? _runCancellation;
    private TaskRecord? _latest;
    private readonly Grid _root;
    private TextBox? _demoInput;
    private ComboBox? _demoName;
    private TextBlock? _resultText;
    private TextBlock? _resultStatus;
    private TextBlock? _progressTitle;
    private TextBlock? _progressDetail;
    private ProgressBar? _progressBar;
    private InfoBar? _notice;
    private Button? _startButton;
    private Button? _cancelButton;
    private Button? _checkWechatButton;
    private Button? _checkVisionButton;
    private CheckBox? _prefill;
    private TextBlock? _prefillHelp;
    private Image? _visionPreview;
    private Border? _visionPreviewCard;
    private ComboBox? _scene;
    private ComboBox? _zodiac;
    private TextBox? _extra;
    private ToggleSwitch? _mode;
    private ComboBox? _windows;
    private List<Contact> _demoContacts = [];
    private string _demoDraft = "";
    private int _demoIndex;
    private long _demoRevision;
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _paused;
    private bool _homeVisible;

    public MainWindow()
    {
        Title = "祝语 · 微信草稿助手";
        _dataDirectory = Environment.GetEnvironmentVariable("WECHAT_DRAFT_DATA") ?? Path.Combine(AppContext.BaseDirectory, "data");
        _repository = new LocalRepository(_dataDirectory, _protector);
        _settings = _repository.LoadSettings();
        var packageWarning = LoadPackagedDefault();
        _repository.RecoverInterrupted(); _repository.Cleanup(_settings.RetentionDays);
        var bundled = Path.Combine(AppContext.BaseDirectory, "config", "blessing_examples.json");
        _examples.Load(bundled);
        if (!string.IsNullOrWhiteSpace(_settings.ExamplesPath)) _examples.TryReload(_settings.ExamplesPath);
        SeedDemoContacts();
        _coordinator = new TaskCoordinator(_repository, _examples, _protector);
        _coordinator.Progress += progress => DispatcherQueue.TryEnqueue(() =>
        {
            ShowProgress(progress.Stage, progress.Detail, progress.Percent);
        });
        _root = new Grid { Background = Ui.Brush("F5F7F5"), RequestedTheme = ElementTheme.Light };
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(BuildSidebar()); Grid.SetColumn(_page, 1); _root.Children.Add(_page);
        Content = _root;
        ConfigureWindowLayout();
        ShowHome();
        if (packageWarning is not null) Notify(packageWarning, InfoBarSeverity.Warning);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hotkey = new GlobalHotkey(hwnd);
        _hotkey.Pressed += () =>
        {
            var recording = _finishHotkeyRecording;
            var shortcut = _settings.GenerationHotkey;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (recording is not null)
                {
                    if (_finishHotkeyRecording == recording) recording(shortcut);
                }
                else if (_finishHotkeyRecording is null && !_paused)
                    _ = StartTaskAsync();
            });
        };
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) _finishHotkeyRecording?.Invoke(null);
        };
        _hotkey.Register(_settings.GenerationHotkey);
        UpdateHotkeyFooter();
        SetupTray();
        ConfigureWindowRefresh();
        Closed += (_, _) => { _historyView?.Dispose(); _runCancellation?.Cancel(); CloseSentRecording(); _hotkey?.Dispose(); _tray?.Dispose(); _client.Dispose(); };
    }

    private void SeedDemoContacts()
        => _demoContacts = DemoContactDefaults.Load(_repository, _settings.AccountScope);

    private Border BuildSidebar()
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var logo = Ui.Row(10, Ui.Card(Ui.Text("语", 22, "FFFFFF", true), "16765C", 9), Ui.Stack(2, Ui.Text("祝语", 23, bold: true), Ui.Text("微信草稿助手", 11, "7B8A83")));
        logo.Margin = new Thickness(4, 14, 0, 10);
        var menu = Ui.Stack(9);
        AddNavigation(menu, "✦   生成祝福", ShowHome);
        AddNavigation(menu, "◷   历史记录", ShowHistory);
        AddNavigation(menu, "✓   已发送祝福", ShowSentGreetings);
        AddNavigation(menu, "♧   联系人档案", ShowContacts);
        AddNavigation(menu, "▤   祝福范例", ShowExamples);
        AddNavigation(menu, "⚡   API 管理", ShowApiProfiles);
        AddNavigation(menu, "⚙   设置", ShowSettings);
        var message = Ui.Card(Ui.Stack(7, Ui.Text("把心意写得恰到好处", 12, "42715F", true), Ui.Text("生成草稿，由你检查和发送。", 11, "70877D")), "EDF5EF", 14);
        layout.Children.Add(new ScrollViewer { Content = Ui.Stack(24, logo, menu, message), VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Enabled });
        var bottom = Ui.Stack(12, BuildSidebarTask(), _footer, Ui.Text("V0.1 · Windows 原型", 11, "94A099")); bottom.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(bottom, 1); layout.Children.Add(bottom);
        return new Border { Child = layout, Background = Ui.Brush("FFFFFF"), BorderBrush = Ui.Brush("E4EAE6"), BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(18) };
    }

    private void AddNavigation(StackPanel parent, string label, Action navigate)
    {
        var button = Ui.Button(label, () => Navigate(navigate));
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
        var background = Ui.Brush("FFFFFF");
        button.Tag = navigate.Method.Name; button.Background = background; button.BorderThickness = new Thickness(0);
        // Share the brush across visual states so hover/press cannot hide a new selection.
        button.Resources["ButtonBackgroundPointerOver"] = background;
        button.Resources["ButtonBackgroundPressed"] = background;
        button.Foreground = Ui.Brush("325347"); parent.Children.Add(button); _navigation.Add(button);
    }

    private void SelectNavigation(string pageName)
    {
        _historyView?.Dispose(); _historyView = null;
        _homeVisible = pageName == nameof(ShowHome);
        if (_homeVisible && !IsTaskBusy) _taskFinishedElsewhere = false;
        _idleActions.Clear();
        _renderSentGreetings = null;
        foreach (var button in _navigation)
            ((SolidColorBrush)button.Background).Color = Ui.Brush(Equals(button.Tag, pageName) ? "EAF3ED" : "FFFFFF").Color;
        RefreshSidebarTask();
    }

    private ScrollViewer Present(StackPanel content)
    {
        content.MaxWidth = 1160; content.HorizontalAlignment = HorizontalAlignment.Stretch;
        var view = new ScrollViewer { Content = content, Padding = new Thickness(32, 26, 32, 30),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Enabled };
        _page.Content = view;
        return view;
    }

    private static Grid Columns(UIElement left, UIElement right, double leftWeight = 1.05, double rightWeight = 1)
    {
        var grid = new Grid { ColumnSpacing = 20 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(leftWeight, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(rightWeight, GridUnitType.Star) });
        grid.Children.Add(left); Grid.SetColumn((FrameworkElement)right, 1); grid.Children.Add(right); return grid;
    }

    private void ShowHome()
    {
        SelectNavigation(nameof(ShowHome));
        _homeVisible = true;
        if (IsTaskBusy && _runningHomeView is not null)
        {
            _page.Content = _runningHomeView;
            return;
        }
        _windows = null;
        _prefill = null; _prefillHelp = null;
        var header = Ui.Stack(8, Ui.Badge("为每一份关系，留一点用心"), Ui.Text("一句祝福，刚刚好。", 30, bold: true),
            Ui.Text("选好场景，打开聊天，让合适的心意落在输入框里。", 14, "73817A"));
        _mode = new ToggleSwitch { Header = "当前工作模式", OffContent = "真实微信", OnContent = "演示模式 · 无 API 调用", IsOn = _settings.DemoMode };
        _mode.Toggled += (_, _) => { if (_coordinator.IsRunning) return; StopSentRecording(); _settings.DemoMode = _mode.IsOn; _repository.SaveSettings(_settings); ShowHome(); };
        _scene = Ui.Combo("祝福场景", ["春节", "元旦", "中秋", "端午", "生日", "纪念日", "通用问候"], _settings.Scene);
        _scene.IsEditable = true; _scene.Text = _settings.Scene;
        _zodiac = BuildZodiacSelector(_scene);
        _extra = Ui.Input("当次补充要求", _settings.ExtraInstruction, "例如：给老师，朴素一点；加 1–2 个 Emoji"); _extra.MaxLength = 300;
        _extra.AcceptsReturn = true; _extra.Height = 86;
        _startButton = Ui.Button(_settings.DemoMode || _settings.EnableLiveAppend ? "✦  生成并预填草稿" : "✦  生成祝福", () => _ = StartTaskAsync(), true);
        _startButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        _cancelButton = Ui.Button("取消任务", () => _runCancellation?.Cancel()); _cancelButton.IsEnabled = false;
        _progressTitle = Ui.Text("准备就绪", 13, "416A56", true);
        _progressDetail = Ui.Text(_settings.DemoMode ? "先体验完整流程，随后在设置中连接你的模型。" : "先配置 API 并选择目标微信窗口。", 12, "7C8981");
        _progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Height = 4, Foreground = Ui.Brush("258260") };
        var form = Ui.Stack(14, Ui.Text("生成一条合适的祝福", 19, bold: true), _mode,
            _scene, _zodiac, _extra, Columns(_startButton, _cancelButton, 3, 1),
            Ui.Stack(7, _progressTitle, _progressDetail, _progressBar));
        if (!_settings.DemoMode)
        {
            _windows = new ComboBox { Header = "目标微信窗口", HorizontalAlignment = HorizontalAlignment.Stretch };
            TrackWindowChoice(_windows);
            RefreshWindows();
            _checkWechatButton = Ui.Button("检查 UIA 输入框", () => _ = CheckWechatAsync());
            _checkVisionButton = Ui.Button("检查视觉定位", () => _ = CheckVisionAsync());
            form.Children.Insert(2, Ui.Stack(8, _windows, Ui.Row(8, _checkWechatButton, _checkVisionButton),
                Ui.Badge(AutomationLabel(_settings.AutomationMode))));
            form.Children.Insert(form.Children.Count - 2, BuildPrefillOption());
        }
        _resultText = Ui.Text(_latest?.Greeting is { Length: > 0 } result ? result : "生成后，祝福正文会显示在这里。", 16, "385347");
        _resultText.IsTextSelectionEnabled = true; _resultText.LineHeight = 29;
        var resultBadge = Ui.Badge(_latest?.FillStatus ?? "等待生成"); _resultStatus = (TextBlock)resultBadge.Child;
        var resultCard = Ui.Card(Ui.Stack(14, Ui.Row(10, Ui.Text("本次祝福", 17, bold: true), resultBadge),
            _resultText, Ui.Button("复制正文", () => CopyText(_latest?.Greeting ?? "")), Ui.Text("结果会自动保存在历史记录中。", 11, "8B958E")), "F0F7F1", 22);
        _visionPreview = null; _visionPreviewCard = null;
        UIElement body;
        if (_settings.DemoMode)
            body = Columns(Ui.Stack(18, Ui.Card(form), resultCard), BuildDemoChat());
        else
        {
            _visionPreview = new Image { Stretch = Stretch.Uniform, MaxHeight = 440 };
            _visionPreviewCard = Ui.Card(Ui.Stack(12,
                Ui.Row(12, Ui.Text("视觉定位预览", 17, bold: true), Ui.Button("收起预览", () =>
                { if (_visionPreviewCard is not null) _visionPreviewCard.Visibility = Visibility.Collapsed; })),
                _visionPreview,
                Ui.Text("黄框：本地图标；蓝框：聊天与标题；绿框：消息；紫框：输入面板；橙框：编辑区估算；红框：发送区域估算。仅预览，不点击或填写。", 12, "71817B")));
            _visionPreviewCard.Visibility = Visibility.Collapsed;
            body = Ui.Stack(18, Ui.Card(form), _visionPreviewCard, resultCard, BuildSentRecordingControls());
        }
        _notice = new InfoBar { IsOpen = false, IsClosable = true };
        _runningHomeView = Present(Ui.Stack(22, header, _notice, body));
        if (_lastTaskProgress is { } progress) ShowProgress(progress.Stage, progress.Detail, progress.Percent);
        if (_lastTaskNotice is { } notice) Notify(notice.Text, notice.Severity);
    }

    private ComboBox BuildZodiacSelector(ComboBox scene)
    {
        var zodiac = Ui.Combo("生肖年", HolidayZodiac.Years, HolidayZodiac.ResolveYear(_settings.ZodiacYear));
        void UpdateVisibility(string sceneName) =>
            zodiac.Visibility = HolidayZodiac.AppliesTo(sceneName) ? Visibility.Visible : Visibility.Collapsed;

        UpdateVisibility(scene.Text);
        scene.SelectionChanged += (_, args) => UpdateVisibility(args.AddedItems.FirstOrDefault() as string ?? scene.Text);
        scene.TextSubmitted += (_, args) => UpdateVisibility(args.Text);
        zodiac.SelectionChanged += (_, _) =>
        {
            if (zodiac.SelectedItem is not string selected) return;
            var previous = HolidayZodiac.ResolveYear(_settings.ZodiacYear);
            if (selected == previous) return;
            var next = _settings with { ZodiacYear = selected };
            try { _repository.SaveSettings(next); _settings = next; }
            catch (Exception e)
            {
                zodiac.SelectedItem = previous;
                Notify("生肖年未保存：" + e.Message, InfoBarSeverity.Warning);
            }
        };
        return zodiac;
    }

    private UIElement BuildDemoChat()
    {
        _demoName = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 160 };
        foreach (var contact in _demoContacts) _demoName.Items.Add(contact.DisplayName);
        _demoName.SelectedIndex = Math.Clamp(_demoIndex, 0, _demoContacts.Count - 1);
        _demoName.SelectionChanged += (_, _) => { _demoIndex = Math.Max(0, _demoName.SelectedIndex); _demoRevision++; };
        _demoInput = new TextBox { Text = _demoDraft, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "可先输入内容，体验保留原文并另起一行追加。", Height = 154, BorderThickness = new Thickness(0), Background = Ui.Brush("FFFFFF") };
        _demoInput.TextChanged += (_, _) => { _demoDraft = _demoInput.Text; _demoRevision++; };
        var otherBubble = Ui.Card(Ui.Text("最近一切都好，祝你也顺顺利利。", 13, "405049"), "FFFFFF", 14);
        otherBubble.HorizontalAlignment = HorizontalAlignment.Left; otherBubble.MaxWidth = 260;
        var ownBubble = Ui.Card(Ui.Text("谢谢，您也多保重！", 13, "2D5040"), "D9EEDB", 14);
        ownBubble.HorizontalAlignment = HorizontalAlignment.Right; ownBubble.MaxWidth = 230;
        var conversation = Ui.Stack(20, Ui.Text("以下为虚构聊天，仅用于演示", 11, "9AA59F"), otherBubble, ownBubble,
            Ui.Text("试试生成一条祝福 ↓", 12, "93A399"));
        conversation.MinHeight = 236;
        var title = Ui.Stack(10, Ui.Row(10, Ui.Badge("演示聊天"), Ui.Badge("不会发送", "748278", "F0F2EF")), _demoName);
        var panel = Ui.Stack(0, Ui.Card(title, "FFFFFF", 18), Ui.Card(conversation, "F2F4F1", 18),
            Ui.Card(Ui.Stack(8, Ui.Text("草稿输入框", 12, "809087"), _demoInput,
                Ui.Row(8, Ui.Button("插入已有草稿", () => _demoInput.Text = "稍后再和您联系。"), Ui.Button("清空", () => _demoInput.Text = "")),
                Ui.Button("模拟发送并归档", SimulateSentGreeting)), "FFFFFF", 16),
            Ui.Text("你可以在生成期间修改草稿，观察系统停止追加。", 11, "8B9990"));
        return panel;
    }

    private async Task StartTaskAsync()
    {
        if (_coordinator.IsRunning || _runCancellation is not null) { Notify("当前任务正在进行，请稍候。", InfoBarSeverity.Informational); return; }
        if (!_homeVisible || _scene is null) ShowHome();
        _settings.Scene = _scene!.Text.Trim();
        _settings.ExtraInstruction = _extra?.Text.Trim() ?? "";
        _repository.SaveSettings(_settings);
        var taskSettings = _settings with { };
        IConversationAdapter adapter;
        Contact? contact = null;
        if (taskSettings.DemoMode)
        {
            if (_demoInput is null) ShowHome();
            contact = _demoContacts[Math.Clamp(_demoIndex, 0, _demoContacts.Count - 1)] with { };
            var input = _demoInput!; var name = _demoName!;
            adapter = new DemoConversationAdapter(() => input.Text, text => input.Text = text,
                () => name.SelectedItem?.ToString() ?? "演示联系人", () => _demoRevision);
        }
        else
        {
            RefreshWindows();
            if (_windows?.SelectedItem is not WechatWindow window) { Notify("请打开微信并选择目标窗口，列表会自动更新。", InfoBarSeverity.Warning); return; }
            if (!await ConfirmScreenshotUploadAsync() || IsTaskBusy) return;
            taskSettings = _settings with { };
            adapter = CreateConversationAdapter(window.Handle, taskSettings);
        }
        _runCancellation = new CancellationTokenSource(); SetBusy(true);
        try
        {
            var result = await _coordinator.RunAsync(taskSettings, adapter,
                taskSettings.DemoMode ? new DemoGreetingModel() : _client, contact, _runCancellation.Token);
            if (result is null) return; _latest = result;
            if (_resultText is not null) _resultText.Text = string.IsNullOrWhiteSpace(result.Greeting) ? "本次没有生成正文。" : result.Greeting;
            if (_resultStatus is not null) _resultStatus.Text = result.FillStatus;
            var detail = result.Error.Length > 0 ? result.Error : "请检查草稿并手动发送。";
            Notify(result.FillStatus + " · " + detail, result.FillStatus is "已预填" or "演示预填" ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            _tray!.Text = "祝语 · " + taskSettings.Scene;
        }
        catch (Exception e)
        {
            ShowProgress("处理失败", e.Message, 100);
            Notify("处理未完成：" + e.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (adapter is IDisposable disposable) disposable.Dispose();
            _runCancellation?.Dispose(); _runCancellation = null; SetBusy(false);
        }
    }

    private async Task CheckWechatAsync()
    {
        if (_coordinator.IsRunning || _runCancellation is not null) return;
        RefreshWindows();
        if (_windows?.SelectedItem is not WechatWindow window)
        { Notify("请打开微信并选择目标窗口，列表会自动更新。", InfoBarSeverity.Warning); return; }
        _runCancellation = new CancellationTokenSource(); SetBusy(true);
        using var debug = DebugSession.Begin(_settings.DebugMode, Path.Combine(AppContext.BaseDirectory, "debug"), "uia-check");
        ShowProgress("检查会话", "仅检查微信输入框，不截图、不调用模型、不写入草稿", 8);
        using var adapter = new WechatAdapter(window.Handle, false);
        ConversationSnapshot? snapshot = null;
        try
        {
            snapshot = await adapter.BeginAsync(_runCancellation.Token);
            debug.WriteJson("uia-check-result", new { snapshot.DisplayName, snapshot.WindowIdentity, snapshot.OriginalTextRead,
                originalCharacters = DraftRules.VisibleLength(snapshot.OriginalText), conversationIdentityAvailable = adapter.CanVerifyConversation });
            var message = $"已读到当前单聊和输入框，可保留原有 {DraftRules.VisibleLength(snapshot.OriginalText)} 个字符。" +
                (adapter.CanVerifyConversation ? "可复查当前聊天标识。" : "尚无可复查的会话标识，仅支持生成。") + "此次仅检查，未生成或写入。";
            ShowProgress("检查通过", message, 100); Notify(message, InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        { ShowProgress("已取消", "微信输入框检查已取消。", 100); Notify("检查已取消。", InfoBarSeverity.Informational); }
        catch (Exception e)
        { debug.WriteJson("uia-check-error", new { type = e.GetType().Name, e.Message, e.StackTrace }); ShowProgress("检查失败", e.Message, 100); Notify(e.Message, InfoBarSeverity.Warning); }
        finally
        {
            if (snapshot is not null) { try { await adapter.RestoreAsync(snapshot); } catch { } }
            _runCancellation.Dispose(); _runCancellation = null; SetBusy(false);
        }
    }

    private IConversationAdapter CreateConversationAdapter(nint target, AppSettings settings)
    {
        if (settings.AutomationMode == "vision") return CreateVisionAdapter(target, settings.EnableLiveAppend);
        var uia = new WechatAdapter(target, settings.EnableLiveAppend, WinRT.Interop.WindowNative.GetWindowHandle(this),
            progress => DispatcherQueue.TryEnqueue(() => ShowProgress(progress.Stage, progress.Detail, progress.Percent)));
        return settings.AutomationMode == "uia" ? uia : new FallbackConversationAdapter(uia, () => CreateVisionAdapter(target, settings.EnableLiveAppend));
    }
    private WechatVisionAdapter CreateVisionAdapter(nint target, bool enableAppend = false) => new(target,
        progress => DispatcherQueue.TryEnqueue(() => ShowProgress(progress.Stage, progress.Detail, progress.Percent)), enableAppend,
        clipboardOwner: WinRT.Interop.WindowNative.GetWindowHandle(this), companion: WinRT.Interop.WindowNative.GetWindowHandle(this));

    private async Task CheckVisionAsync()
    {
        if (_coordinator.IsRunning || _runCancellation is not null) return;
        RefreshWindows();
        if (_windows?.SelectedItem is not WechatWindow window)
        { Notify("请打开微信并选择目标窗口，列表会自动更新。", InfoBarSeverity.Warning); return; }
        _runCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_settings.TaskTimeoutSeconds, 10, 180)));
        SetBusy(true);
        using var debug = DebugSession.Begin(_settings.DebugMode, Path.Combine(AppContext.BaseDirectory, "debug"), "layout-check");
        using var adapter = CreateVisionAdapter(window.Handle);
        try
        {
            if (_visionPreviewCard is not null) _visionPreviewCard.Visibility = Visibility.Collapsed;
            var layout = await adapter.CheckLayoutAsync(_runCancellation.Token);
            if (adapter.PreviewPng is { } png && _visionPreview is not null)
            {
                using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new global::Windows.Storage.Streams.DataWriter(stream))
                { writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); }
                stream.Seek(0);
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(); await bitmap.SetSourceAsync(stream);
                _visionPreview.Source = bitmap; _visionPreview.Visibility = Visibility.Visible;
                if (_visionPreviewCard is not null) _visionPreviewCard.Visibility = Visibility.Visible;
            }
            var detail = "本地双锚点及区域分界定位完成，模型定位请求 0 次。区域为几何估算，尚未确认聊天身份或类型；未读取草稿、未填写。";
            ShowProgress("视觉定位通过", detail, 100); Notify(detail, InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { ShowProgress("已取消或超时", "视觉检查已结束，未填写。", 100); }
        catch (Exception e) { debug.WriteJson("layout-check-error", new { type = e.GetType().Name, e.Message, e.StackTrace }); ShowProgress("视觉定位失败", e.Message, 100); Notify(e.Message, InfoBarSeverity.Warning); }
        finally { _runCancellation.Dispose(); _runCancellation = null; SetBusy(false); }
    }

    private static string AutomationLabel(string mode) => mode switch { "uia" => "仅 UIA", "vision" => "仅视觉", _ => "自动 · UIA 优先" };

    private void ShowProgress(string stage, string detail, int percent)
    {
        _lastTaskProgress = new(stage, detail, percent);
        if (_progressTitle is not null) _progressTitle.Text = stage;
        if (_progressDetail is not null) _progressDetail.Text = detail;
        if (_progressBar is not null) _progressBar.Value = percent;
        RefreshSidebarTask();
    }

    private void SetBusy(bool busy)
    {
        _sentMonitor?.Pause(busy);
        if (_sentStartButton is not null) _sentStartButton.IsEnabled = !busy;
        if (busy)
        {
            _homeSettingsAtTaskStart = _settings with { };
            _taskFinishedElsewhere = false;
            _lastTaskNotice = null;
            if (_notice is not null) _notice.IsOpen = false;
            ShowProgress("准备任务", "正在准备本次任务", 0);
        }
        if (_startButton is not null) _startButton.IsEnabled = !busy;
        if (_cancelButton is not null) _cancelButton.IsEnabled = busy;
        if (_checkWechatButton is not null) _checkWechatButton.IsEnabled = !busy;
        if (_checkVisionButton is not null) _checkVisionButton.IsEnabled = !busy;
        if (_prefill is not null) _prefill.IsEnabled = !busy;
        if (_windows is not null) _windows.IsEnabled = !busy;
        if (_zodiac is not null) _zodiac.IsEnabled = !busy;
        if (_mode is not null) _mode.IsEnabled = !busy;
        foreach (var button in _idleActions) button.IsEnabled = !busy;
        _historyView?.SetTaskBusy(busy);
        if (!busy) _taskFinishedElsewhere = !_homeVisible;
        RefreshSidebarTask();
        if (!busy)
        {
            var refreshHome = _homeVisible && _homeSettingsAtTaskStart is not null && _homeSettingsAtTaskStart != _settings;
            _homeSettingsAtTaskStart = null;
            if (refreshHome) ShowHome(); else RefreshWindows();
        }
    }
    private void Notify(string text, InfoBarSeverity severity)
    {
        _lastTaskNotice = (text, severity);
        if (_notice is null) return; _notice.Message = text; _notice.Severity = severity; _notice.IsOpen = true;
    }
    private static string ToneLabel(string tone) => tone switch { "formal" => "正式得体", "warm" => "亲切轻松", _ => "自然温和" };
    private void CopyText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var data = new DataPackage(); data.SetText(value); Clipboard.SetContent(data);
            Notify("正文已复制。", InfoBarSeverity.Success);
        }
        catch { Notify("剪贴板正忙，请稍后重试。", InfoBarSeverity.Warning); }
    }
    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon { Text = "祝语 · " + _settings.Scene,
            Icon = System.Drawing.SystemIcons.Information, Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开祝语", null, (_, _) => DispatcherQueue.TryEnqueue(Activate));
        menu.Items.Add("暂停 / 恢复快捷键", null, (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _paused = !_paused;
            if (_paused) _hotkey?.Pause();
            else _hotkey?.Register(_settings.GenerationHotkey);
            UpdateHotkeyFooter();
        }));
        menu.Items.Add("退出", null, (_, _) => DispatcherQueue.TryEnqueue(Close));
        _tray.ContextMenuStrip = menu; _tray.DoubleClick += (_, _) => DispatcherQueue.TryEnqueue(Activate);
    }
    private void UpdateHotkeyFooter()
    {
        _footer.Text = _paused ? "快捷键已暂停" :
            _hotkey?.IsRegistered == true ? _settings.GenerationHotkey.ToString() : "快捷键不可用，请到设置修改";
    }
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, PrimaryButtonText = "确认", CloseButtonText = "取消", XamlRoot = _root.XamlRoot };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
