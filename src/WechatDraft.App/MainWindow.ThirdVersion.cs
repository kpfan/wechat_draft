using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;
using WechatDraft.Core.ThirdVersion;
using WechatDraft.Windows;
using Windows.Storage.Pickers;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private ThirdVersionService? _thirdService;
    private ThirdVersionSession? _thirdSession;
    private ScrollViewer? _thirdView;
    private TextBox? _thirdScene, _thirdExtra, _thirdManual, _thirdInstruction;
    private TextBlock? _thirdStatus, _thirdEvidence, _thirdCurrent, _thirdTurns, _thirdFiles;
    private StackPanel? _thirdCandidates, _thirdUnknown;
    private ComboBox? _thirdWindows;
    private readonly List<Control> _thirdActions = [];
    private readonly List<Capture> _thirdImported = [];
    private int _thirdSelected;
    private bool _thirdBusy;
    private string _thirdDisplayName = "截图实验";
    private TaskRecord? _thirdHistory;

    private ThirdVersionService ThirdService => _thirdService ??= new ThirdVersionService(
        Path.Combine(AppContext.BaseDirectory, "config", "third-version"));

    private Button ThirdButton(string label, Action action, bool primary = false)
    {
        var button = Ui.Button(label, action, primary);
        button.IsEnabled = !_thirdBusy;
        _thirdActions.Add(button);
        return button;
    }

    private void ShowThirdVersion()
    {
        SelectNavigation(nameof(ShowThirdVersion));
        if (_thirdView is not null) { _page.Content = _thirdView; return; }
        _thirdScene = Ui.Input("祝福场景", _settings.Scene, "春节、教师节、生日……");
        _thirdScene.MaxLength = 100;
        _thirdExtra = Ui.Input("当次补充要求", _settings.ExtraInstruction, "例如：再朴素一点；不要表情");
        _thirdExtra.AcceptsReturn = true; _thirdExtra.MaxLength = 300;
        _thirdWindows = new ComboBox { Header = "采集目标（只读，不预填）", HorizontalAlignment = HorizontalAlignment.Stretch };
        _thirdActions.Add(_thirdWindows);
        ThirdRefreshWindows();
        _thirdFiles = Ui.Text("尚未导入截图。支持 1–4 张 PNG，总计不超过 12 MiB。", 12, "71817B");
        _thirdStatus = Ui.Text("准备就绪。可先点“离线演示”检查完整交互，不调用模型。", 13, "416A56");
        _thirdEvidence = Ui.Text("提取结果会在这里显示。", 13);
        _thirdEvidence.IsTextSelectionEnabled = true;
        _thirdManual = Ui.Input("本次称呼", "", "例如：王老师（仅本次使用，不保存联系人）"); _thirdManual.MaxLength = 30;
        _thirdUnknown = Ui.Stack(10, Ui.Text("暂时无法确定称呼，请选择下一步", 16, bold: true),
            _thirdManual, ThirdButton("使用填写的称呼生成", () => _ = ThirdResolveAsync(false)),
            ThirdButton("不带称呼，生成中性祝福", () => _ = ThirdResolveAsync(true)));
        _thirdUnknown.Visibility = Visibility.Collapsed;
        _thirdCandidates = Ui.Stack(10);
        _thirdCurrent = Ui.Text("先生成三个候选，再选择要使用或修改的一条。", 16); _thirdCurrent.IsTextSelectionEnabled = true;
        _thirdCurrent.LineHeight = 28;
        _thirdInstruction = Ui.Input("修改意见", "", "例如：再短一点；还是用您吧；去掉感叹号");
        _thirdInstruction.AcceptsReturn = true; _thirdInstruction.MaxLength = 1000;
        _thirdTurns = Ui.Text("当前候选还没有修改记录。", 12, "71817B"); _thirdTurns.IsTextSelectionEnabled = true;
        var content = Ui.Stack(18,
            Ui.Text("第三版实验", 28, bold: true),
            Ui.Text("先提取称呼，再生成三个候选。支持重新生成、候选独立的多轮修改和称呼确认。沿用 API 管理中选定的服务。", 13, "71817B"),
            Ui.Card(Ui.Stack(12, _thirdScene, _thirdExtra, _thirdWindows,
                ThirdButton("刷新微信窗口", ThirdRefreshWindows),
                ThirdButton("只读采集当前聊天并生成", () => _ = ThirdCaptureAsync(), true),
                ThirdButton("导入聊天截图…", () => _ = ThirdImportAsync()), _thirdFiles,
                ThirdButton("使用导入截图生成", () => _ = ThirdFromImportedAsync()),
                ThirdButton("离线演示（不调用模型）", ThirdDemo),
                Ui.Button("取消当前操作", () => _runCancellation?.Cancel()), _thirdStatus)),
            Ui.Card(Ui.Stack(12, Ui.Text("称呼与依据", 18, bold: true), _thirdEvidence, _thirdUnknown)),
            Ui.Card(Ui.Stack(12, Ui.Text("三个候选", 18, bold: true), _thirdCandidates,
                ThirdButton("重新生成三个候选", () => _ = ThirdRegenerateAsync()),
                ThirdButton("按提取结果重试首次生成", () => _ = ThirdRetryGenerateAsync()))),
            Ui.Card(Ui.Stack(12, Ui.Text("当前选择与多轮修改", 18, bold: true), _thirdCurrent,
                ThirdButton("复制当前正文", ThirdCopy), _thirdInstruction,
                ThirdButton("按意见修改", () => _ = ThirdReviseAsync(), true),
                ThirdButton("重置当前候选的修改", ThirdReset), _thirdTurns)),
            Ui.Card(Ui.Stack(8, Ui.Text("提示词与示例", 18, bold: true),
                Ui.Text("修改 config/third-version 中的规则或示例，下次生成读取新内容。格式错误会明确提示。示例与旧版“祝福范例”分开，避免重复注入。", 12, "71817B"),
                ThirdButton("打开提示词与示例目录", () => global::Windows.System.Launcher.LaunchFolderPathAsync(
                    Path.Combine(AppContext.BaseDirectory, "config", "third-version")).AsTask().ContinueWith(_ => { })))),
            Ui.Text("本实验入口只生成、复制并保存选中文案到本地加密历史，不自动预填、不发送。更换截图会开始新会话；原来的生成与安全预填入口保持不变。", 12, "8B7953"));
        _thirdView = Present(content);
    }

    private void ThirdRefreshWindows()
    {
        if (_thirdWindows is null || _thirdBusy) return;
        try
        {
            var old = _thirdWindows.SelectedItem as WechatWindow;
            var windows = WechatAdapter.FindWindows().ToArray();
            _thirdWindows.Items.Clear();
            foreach (var window in windows) _thirdWindows.Items.Add(window);
            _thirdWindows.SelectedItem = WechatWindowSelection.Select(windows, old);
            _thirdWindows.PlaceholderText = windows.Length == 0 ? "未找到微信，可先导入截图或离线演示" : "请选择微信窗口";
        }
        catch (Exception e) { if (_thirdStatus is not null) _thirdStatus.Text = "枚举窗口失败：" + e.Message; }
    }

    private AppSettings ThirdSettings() => _settings with
    {
        Scene = _thirdScene!.Text.Trim(), ExtraInstruction = _thirdExtra!.Text.Trim(),
        DemoMode = false, EnableLiveAppend = false, ApiProfiles = _settings.ApiProfiles.ToList()
    };

    private async Task ThirdRunAsync(string stage, Func<CancellationToken, Task> operation)
    {
        if (IsTaskBusy || _thirdBusy) { _thirdStatus!.Text = "已有任务正在进行，请先完成或取消。"; return; }
        _runCancellation = new CancellationTokenSource();
        _thirdBusy = true;
        foreach (var control in _thirdActions) control.IsEnabled = false;
        _thirdScene!.IsEnabled = _thirdExtra!.IsEnabled = _thirdManual!.IsEnabled = _thirdInstruction!.IsEnabled = false;
        SetBusy(true);
        _thirdStatus!.Text = stage;
        try
        {
            await operation(_runCancellation.Token);
            _thirdStatus.Text = $"操作完成 · 累计实际模型请求 {_thirdSession?.ApiCallCount ?? 0} 次 · 只生成，不预填";
        }
        catch (OperationCanceledException) { _thirdStatus.Text = "已取消或超过时间预算。没有预填或发送。已有候选仍可查看。"; }
        catch (Exception e) { _thirdStatus.Text = "操作失败：" + e.Message; }
        finally
        {
            _runCancellation.Dispose(); _runCancellation = null;
            _thirdBusy = false;
            foreach (var control in _thirdActions) control.IsEnabled = true;
            _thirdScene!.IsEnabled = _thirdExtra!.IsEnabled = _thirdManual!.IsEnabled = _thirdInstruction!.IsEnabled = true;
            SetBusy(false);
            ThirdRender();
        }
    }

    private async Task ThirdImportAsync()
    {
        if (IsTaskBusy || _thirdBusy) return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".png");
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;
            if (files.Count > 4) throw new InvalidDataException("最多选择四张截图。");
            var selected = new List<Capture>(); long total = 0;
            foreach (var file in files)
            {
                var info = new FileInfo(file.Path); total += info.Length;
                if (info.Length == 0 || total > 12 * 1024 * 1024) throw new InvalidDataException("截图为空或总量超过 12 MiB。");
                var bytes = await File.ReadAllBytesAsync(file.Path);
                if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidDataException("请选择有效的 PNG 图片。");
                selected.Add(new Capture("import_" + selected.Count, bytes, true));
            }
            _thirdImported.Clear(); _thirdImported.AddRange(selected);
            _thirdFiles!.Text = $"已导入 {selected.Count} 张 PNG。仅在点击生成后，向已授权服务上传。";
            _thirdStatus!.Text = "截图已准备好；点击“使用导入截图生成”开始新会话。";
        }
        catch (Exception e) { _thirdStatus!.Text = "导入失败：" + e.Message; }
    }

    private Task ThirdFromImportedAsync() => ThirdRunAsync("正在提取截图中的称呼……", async ct =>
    {
        if (_thirdImported.Count == 0) throw new InvalidDataException("请先导入聊天截图。");
        _thirdDisplayName = "截图实验"; _thirdHistory = null;
        await ThirdExtractAsync(ThirdSettings(), _thirdImported.ToArray(), null, new([]), ct);
    });

    private Task ThirdCaptureAsync() => ThirdRunAsync("准备只读采集；请切回所选微信聊天……", async ct =>
    {
        if (_thirdWindows?.SelectedItem is not WechatWindow window) throw new InvalidDataException("请先选择微信窗口。");
        var settings = ThirdSettings();
        // Consent is checked before taking a screenshot; this page never receives append authority.
        if (settings.AuthorizedOrigin != DraftRules.Origin(settings)) throw new InvalidDataException("请先在 API 管理授权向当前服务上传必要聊天截图。");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TaskTimeoutSeconds, 10, 180)));
        IConversationAdapter adapter = CreateConversationAdapter(window.Handle, settings);
        ConversationSnapshot? snapshot = null;
        try
        {
            // A short visible countdown gives the user time to foreground WeChat; no activation/click is issued.
            for (var seconds = 3; seconds > 0; seconds--)
            { _thirdStatus!.Text = $"{seconds} 秒后只读采集，请切回目标微信聊天。不会自动点击或填写。"; await Task.Delay(1000, budget.Token); }
            snapshot = await adapter.BeginAsync(budget.Token);
            if (snapshot.DisplayNameRead && DraftRules.LooksLikeGroupTitle(snapshot.DisplayName)) throw new GroupChatDetectedException();
            var capture = await adapter.CaptureAsync(snapshot, 0, budget.Token);
            _thirdDisplayName = snapshot.DisplayName;
            var contact = snapshot.DisplayNameRead ? _repository.FindContactByName(settings.AccountScope, snapshot.DisplayName) : null;
            var context = new GreetingGenerationContext(_repository.RecentGreetings(settings.AccountScope, contact?.Id,
                snapshot.DisplayName, settings.Scene, false, budget.Token));
            _thirdHistory = null;
            await ThirdExtractAsync(settings, [capture], contact, context, budget.Token);
        }
        finally
        {
            if (snapshot is not null) { try { await adapter.RestoreAsync(snapshot); } catch { } }
            if (adapter is IDisposable disposable) disposable.Dispose();
        }
    });

    private async Task ThirdExtractAsync(AppSettings settings, IReadOnlyList<Capture> captures, Contact? contact,
        GreetingGenerationContext context, CancellationToken ct)
    {
        // Drop the old branch before extraction so failed new input can never be resolved against an older chat.
        _thirdSession = null;
        var session = await ThirdService.ExtractAsync(settings, _protector.Unprotect(settings.ProtectedApiKey), captures, contact, context, ct);
        _thirdSession = session; _thirdSelected = 0;
        if (session.Extraction.Name is not null || contact is { Confirmed: true })
        {
            _thirdStatus!.Text = "称呼提取完成，正在生成三个候选……";
            await ThirdService.GenerateAsync(session, null, contact is { Confirmed: true } && string.IsNullOrWhiteSpace(contact.AddressTerm), ct);
            ThirdSaveSelection();
        }
        // Unknown leaves session visible and waits for explicit user action; no model generation is issued.
    }

    private Task ThirdRetryGenerateAsync() => ThirdRunAsync("正在使用已有提取结果重新生成……", async ct =>
    {
        var session = _thirdSession ?? throw new InvalidDataException("请先提取截图。");
        if (session.IsDemo) throw new InvalidDataException("离线演示只供查看，真实生成请导入截图并配置 API。");
        if (session.Candidates.Count > 0) throw new InvalidDataException("已有候选，请使用重新生成。");
        await ThirdService.GenerateAsync(session, null, session.Contact is { Confirmed: true } contact && string.IsNullOrWhiteSpace(contact.AddressTerm), ct);
        _thirdSelected = 0; ThirdSaveSelection();
    });

    private Task ThirdResolveAsync(bool neutral) => ThirdRunAsync("正在按你的称呼选择生成三个候选……", async ct =>
    {
        var session = _thirdSession ?? throw new InvalidDataException("请先提取截图。");
        var address = _thirdManual!.Text.Trim();
        if (!neutral && address.Length == 0) throw new InvalidDataException("请填写本次称呼，或选择中性祝福。");
        await ThirdService.GenerateAsync(session, neutral ? null : address, neutral, ct);
        _thirdSelected = 0; ThirdSaveSelection();
    });

    private Task ThirdRegenerateAsync() => ThirdRunAsync("正在重新生成三个候选……", async ct =>
    {
        var session = _thirdSession ?? throw new InvalidDataException("请先生成候选。");
        await ThirdService.RegenerateAsync(session, ct);
        _thirdSelected = 0; _thirdHistory = null; ThirdSaveSelection();
    });

    private Task ThirdReviseAsync() => ThirdRunAsync("正在修改当前候选（保留该候选的上下文）……", async ct =>
    {
        var session = _thirdSession ?? throw new InvalidDataException("请先生成候选。");
        await ThirdService.ReviseAsync(session, _thirdSelected, _thirdInstruction!.Text.Trim(), ct);
        _thirdInstruction.Text = ""; ThirdSaveSelection();
    });

    private void ThirdReset()
    {
        if (_thirdBusy || IsTaskBusy || _thirdSession?.Candidates.Count != 3) return;
        if (_thirdSession.IsDemo) { _thirdStatus!.Text = "离线演示没有修改历史，无需重置。"; return; }
        ThirdService.ResetRevision(_thirdSession, _thirdSelected);
        ThirdSaveSelection(); ThirdRender();
    }

    private void ThirdDemo()
    {
        if (_thirdBusy || IsTaskBusy) return;
        _thirdSession = ThirdService.CreateDemoSession(_thirdScene!.Text.Trim(), _thirdExtra!.Text.Trim());
        _thirdSelected = 0; _thirdDisplayName = "离线演示联系人"; _thirdHistory = null;
        _thirdStatus!.Text = "离线演示：虚构联系人，不调用模型、不采集微信。可切换候选及复制；真实重生成和修改请导入截图并配置 API。";
        ThirdRender();
    }

    private string ThirdCurrentText() => _thirdSession is { Candidates.Count: 3, Revisions.Count: 3 } session
        ? session.Revisions[Math.Clamp(_thirdSelected, 0, 2)].Greeting : "";

    private void ThirdCopy()
    {
        var text = ThirdCurrentText();
        if (text.Length == 0) { _thirdStatus!.Text = "请先生成并选择候选。"; return; }
        CopyText(text); ThirdSaveSelection();
        _thirdStatus!.Text = "已复制当前正文。请自行检查并手动发送。";
    }

    private void ThirdSaveSelection()
    {
        var session = _thirdSession; var text = ThirdCurrentText();
        if (session is null || text.Length == 0 || session.IsDemo) return;
        _thirdHistory ??= new TaskRecord { AccountScope = session.Settings.AccountScope, Scene = session.Settings.Scene,
            DisplayName = _thirdDisplayName, Model = session.Settings.Model, AutomationMethod = "third-version-experiment",
            FillStatus = "待手动处理", IsDemo = false };
        _thirdHistory.Greeting = text; _thirdHistory.GeneratedAt = DateTimeOffset.UtcNow;
        _thirdHistory.GenerationStatus = "成功"; _thirdHistory.CompletedAt = DateTimeOffset.UtcNow;
        _thirdHistory.ApiCallCount = session.ApiCallCount; _thirdHistory.CaptureCount = session.Captures.Count;
        _thirdHistory.AddressTerm = session.ResolvedAddress;
        _thirdHistory.AddressTermSource = session.Neutral ? "unknown" : session.Extraction.Name == session.ResolvedAddress ? "third_version_" + session.Extraction.Basis : "user";
        _thirdHistory.Error = "第三版实验：仅生成和复制，没有申请微信预填权限。";
        _repository.SaveTask(_thirdHistory);
    }

    private void ThirdRender()
    {
        if (_thirdEvidence is null || _thirdSession is not { } session)
        {
            if (_thirdCandidates is not null) _thirdCandidates.Children.Clear();
            if (_thirdUnknown is not null) _thirdUnknown.Visibility = Visibility.Collapsed;
            if (_thirdEvidence is not null) _thirdEvidence.Text = "尚未获得提取结果。";
            if (_thirdCurrent is not null) _thirdCurrent.Text = "请先提取截图并生成候选。";
            return;
        }
        foreach (var control in _thirdActions.OfType<Button>())
        {
            if (control.Content is string label && (label is "重新生成三个候选" or "按提取结果重试首次生成" or "按意见修改" or "重置当前候选的修改"))
                control.IsEnabled = !_thirdBusy && !session.IsDemo;
        }
        var extraction = session.Extraction;
        _thirdEvidence.Text = $"提取称呼：{extraction.Name ?? "无法确定"} · 判断类型：{extraction.Basis}\n依据：{extraction.Evidence ?? "无清晰依据"}\n" +
            (session.Candidates.Count > 0 ? $"本次使用：{(session.Neutral ? "不带称呼" : session.ResolvedAddress)}\n" : "") +
            $"场景：{session.Settings.Scene} · 实际模型请求：{session.ApiCallCount}";
        _thirdUnknown!.Visibility = extraction.Name is null && session.Candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _thirdCandidates!.Children.Clear();
        for (var i = 0; i < session.Candidates.Count; i++)
        {
            var index = i;
            var select = Ui.Button(($"{(i == _thirdSelected ? "✓ " : "")}选择候选 {i + 1}"), () =>
            {
                if (_thirdBusy) return;
                _thirdSelected = index; ThirdSaveSelection(); ThirdRender();
            }); select.IsEnabled = !_thirdBusy;
            var body = Ui.Text(session.Candidates[i], 14); body.IsTextSelectionEnabled = true;
            _thirdCandidates.Children.Add(Ui.Card(Ui.Stack(8, select, body), i == _thirdSelected ? "F0F7F1" : "FFFFFF", 14));
        }
        _thirdCurrent!.Text = ThirdCurrentText() is { Length: > 0 } text ? text : "请选择如何处理称呼，再生成候选。";
        if (session.Revisions.Count > _thirdSelected)
        {
            var revision = session.Revisions[_thirdSelected];
            _thirdTurns!.Text = (revision.Note.Length > 0 ? "说明：" + revision.Note + "\n\n" : "") +
                (revision.Turns.Count == 0 ? "当前候选还没有修改记录。" : string.Join("\n\n", revision.Turns.Select((turn, index) =>
                    $"第 {index + 1} 轮 · {turn.Instruction}\n{turn.Greeting}" + (turn.Note.Length > 0 ? "\n说明：" + turn.Note : ""))));
        }
        else _thirdTurns!.Text = "当前候选还没有修改记录。";
    }
}
