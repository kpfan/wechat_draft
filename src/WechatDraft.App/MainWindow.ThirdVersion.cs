using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;
using WechatDraft.Core.ThirdVersion;
using WechatDraft.Windows;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private ThirdVersionService? _thirdService;
    private ThirdVersionWorkflow? _thirdWorkflow;
    private ThirdVersionRun? _thirdRun;
    private DispatcherTimer? _thirdExpiryTimer;
    private TextBlock? _thirdEvidence, _thirdCurrent, _thirdTurns, _thirdState;
    private TextBox? _thirdManual, _thirdInstruction;
    private StackPanel? _thirdCandidates, _thirdUnknown;
    private Button? _thirdResolve, _thirdNeutral, _thirdRegenerate, _thirdRetry, _thirdRevise, _thirdReset, _thirdAppend, _thirdCopy;
    private readonly List<Control> _thirdActions = [];

    private ThirdVersionWorkflow ThirdWorkflow
    {
        get
        {
            if (_thirdWorkflow is not null) return _thirdWorkflow;
            _thirdService = new ThirdVersionService(Path.Combine(AppContext.BaseDirectory, "config", "third-version"));
            _thirdWorkflow = new ThirdVersionWorkflow(_repository, _protector, _thirdService);
            _thirdWorkflow.Progress += progress => DispatcherQueue.TryEnqueue(() => ShowProgress(progress.Stage, progress.Detail, progress.Percent));
            return _thirdWorkflow;
        }
    }

    private Button ThirdAction(string text, Action action, bool primary = false)
    {
        var button = Ui.Button(text, action, primary);
        _thirdActions.Add(button);
        return button;
    }

    // This is the result area of the existing real-WeChat home, not a separate page.
    private UIElement BuildThirdVersionResult()
    {
        _thirdActions.Clear();
        _thirdEvidence = Ui.Text("自动采集当前微信聊天后，先提取称呼，再生成三个候选。", 13, "71817B");
        _thirdEvidence.IsTextSelectionEnabled = true;
        _thirdState = Ui.Text("尚未生成。", 12, "71817B");
        _thirdManual = Ui.Input("本次称呼", "", "例如：王老师；仅本次使用，不修改联系人档案");
        _thirdManual.MaxLength = 30;
        _thirdResolve = ThirdAction("使用填写的称呼生成", () => _ = ThirdOperateAsync(ct => ThirdWorkflow.GenerateAsync(RequireThirdRun(), _thirdManual.Text.Trim(), false, ct)));
        _thirdNeutral = ThirdAction("不带称呼，生成中性祝福", () => _ = ThirdOperateAsync(ct => ThirdWorkflow.GenerateAsync(RequireThirdRun(), null, true, ct)));
        _thirdUnknown = Ui.Stack(10, Ui.Text("称呼不确定，请选择下一步", 16, bold: true), _thirdManual, Ui.Row(8, _thirdResolve, _thirdNeutral));
        _thirdCandidates = Ui.Stack(12);
        _thirdRegenerate = ThirdAction("重新生成三个候选", () => _ = ThirdOperateAsync(ct => ThirdWorkflow.RegenerateAsync(RequireThirdRun(), ct)));
        _thirdRetry = ThirdAction("按已有提取结果重试生成", () => _ = ThirdOperateAsync(ct =>
        {
            var run = RequireThirdRun();
            return ThirdWorkflow.GenerateAsync(run, null, run.Session?.Contact is { Confirmed: true } contact && string.IsNullOrWhiteSpace(contact.AddressTerm), ct);
        }));
        _thirdCurrent = Ui.Text("生成后请先选择一条候选，再修改、复制或预填。", 16, "385347");
        _thirdCurrent.IsTextSelectionEnabled = true; _thirdCurrent.LineHeight = 29;
        _thirdInstruction = Ui.Input("修改意见", "", "例如：再短一点；更正式一些；去掉表情");
        _thirdInstruction.AcceptsReturn = true; _thirdInstruction.MaxLength = 1000;
        _thirdRevise = ThirdAction("按意见修改当前候选", () => _ = ThirdOperateAsync(async ct =>
        {
            var run = RequireThirdRun();
            var result = await ThirdWorkflow.ReviseAsync(run, run.SelectedIndex, _thirdInstruction.Text.Trim(), ct);
            if (result is not null) _thirdInstruction.Text = "";
        }), true);
        _thirdReset = ThirdAction("重置当前候选的修改", () => ThirdSyncAction(() =>
        {
            var run = RequireThirdRun(); ThirdWorkflow.Reset(run, run.SelectedIndex);
        }));
        _thirdCopy = ThirdAction("复制当前正文", () => CopyText(ThirdSelectedText()));
        _thirdAppend = ThirdAction("预填当前选中正文", () => _ = ThirdOperateAsync(ct => ThirdWorkflow.AppendSelectedAsync(RequireThirdRun(), ct)), true);
        _thirdTurns = Ui.Text("每条候选保留自己的修改记录，切换不会串上下文。", 12, "71817B");
        _thirdTurns.IsTextSelectionEnabled = true;
        var content = Ui.Stack(14,
            Ui.Text("本次祝福 · 三个候选", 19, bold: true), _thirdEvidence, _thirdUnknown,
            Ui.Row(8, _thirdRegenerate, _thirdRetry), _thirdCandidates,
            Ui.Text("当前选中正文", 17, bold: true), _thirdCurrent,
            _thirdInstruction, Ui.Row(8, _thirdRevise, _thirdReset), _thirdTurns,
            Ui.Row(8, _thirdCopy, _thirdAppend), _thirdState,
            Ui.Text("候选需由你明确选择。预填只尝试一次，不发送；视觉模式由上游适配器核对当前聊天并保留填写时的完整草稿，UIA 要求原稿不变。超过 10 分钟或强制生成时只能复制。", 12, "8D713D"),
            Ui.Button("打开提示词与示例目录", () => _ = OpenThirdPromptsAsync()));
        ThirdRenderHome();
        return Ui.Card(content, "F0F7F1", 22);
    }

    private async Task OpenThirdPromptsAsync()
    {
        try { await global::Windows.System.Launcher.LaunchFolderPathAsync(Path.Combine(AppContext.BaseDirectory, "config", "third-version")); }
        catch (Exception e) { Notify("打开提示词目录失败：" + e.Message, InfoBarSeverity.Warning); }
    }

    private ThirdVersionRun RequireThirdRun() => _thirdRun ?? throw new InvalidOperationException("请先点击生成祝福，自动采集当前微信聊天。");

    private async Task StartThirdVersionTaskAsync(AppSettings settings, WechatWindow window, bool forceGeneration = false)
    {
        await ThirdOperateAsync(async ct =>
        {
            if (_thirdRun is not null) await ThirdWorkflow.ReleaseAsync(_thirdRun);
            _thirdRun = null;
            // The original adapter decides UIA/visual routing, foreground waits, screenshot bounds and identity checks.
            var adapter = forceGeneration ? CreateVisionAdapter(window.Handle, enableAppend: false, forceGeneration: true)
                : CreateConversationAdapter(window.Handle, settings);
            _thirdRun = await ThirdWorkflow.StartAsync(settings, adapter, ct);
            StartThirdExpiryTimer();
        });
    }

    private async Task ThirdOperateAsync(Func<CancellationToken, Task> operation)
    {
        if (IsTaskBusy) { Notify("当前任务正在进行，请稍候。", InfoBarSeverity.Informational); return; }
        _runCancellation = new CancellationTokenSource();
        SetBusy(true); ThirdSetBusy(true);
        try { await operation(_runCancellation.Token); }
        catch (OperationCanceledException) { Notify("已取消，没有重复预填。已有候选仍可查看。", InfoBarSeverity.Warning); }
        catch (Exception e) { Notify("处理未完成：" + e.Message, InfoBarSeverity.Error); }
        finally
        {
            _runCancellation.Dispose(); _runCancellation = null;
            if (_thirdRun is { } run)
            {
                _latest = run.Record;
                if (_resultText is not null) _resultText.Text = ThirdSelectedText();
                if (_resultStatus is not null) _resultStatus.Text = run.Record.FillStatus;
                var detail = run.Record.Error.Length > 0 ? run.Record.Error : run.Session?.Candidates.Count == 3 ? "请选择一条候选。" : "请确认本次称呼。";
                Notify(run.Record.FillStatus + " · " + detail, run.Record.GenerationStatus == "失败" ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
            }
            SetBusy(false); ThirdSetBusy(false); ThirdRenderHome();
        }
    }

    private void ThirdSyncAction(Action action)
    {
        if (IsTaskBusy) return;
        try { action(); _latest = _thirdRun?.Record; ThirdRenderHome(); }
        catch (Exception e) { Notify(e.Message, InfoBarSeverity.Warning); }
    }

    private string ThirdSelectedText() => _thirdRun is { Session: { Revisions.Count: 3 } session, SelectedIndex: >= 0 and < 3 } run
        ? session.Revisions[run.SelectedIndex].Greeting : "";

    private void ThirdSetBusy(bool busy)
    {
        foreach (var control in _thirdActions) control.IsEnabled = !busy;
        if (_thirdManual is not null) _thirdManual.IsEnabled = !busy;
        if (_thirdInstruction is not null) _thirdInstruction.IsEnabled = !busy;
    }

    private void ThirdRenderHome()
    {
        if (_thirdEvidence is null || _thirdCandidates is null || _thirdUnknown is null) return;
        var run = _thirdRun; var session = run?.Session; var busy = IsTaskBusy;
        var generated = session?.Candidates.Count == 3;
        var selected = generated && run!.SelectedIndex is >= 0 and < 3;
        var editable = run?.Record.AppendAttemptId is null;
        _thirdUnknown.Visibility = session is not null && !generated && session.Extraction.Name is null && session.Contact is not { Confirmed: true }
            ? Visibility.Visible : Visibility.Collapsed;
        _thirdEvidence.Text = session is null ? "点击上方“生成祝福”，自动截图并提取称呼，不需要导入图片。" :
            $"称呼：{session.Extraction.Name ?? "无法确定"} · 判断：{session.Extraction.Basis}\n依据：{session.Extraction.Evidence ?? "无清晰依据"}\n" +
            $"本次场景：{session.Settings.Scene} · 已采集 {run!.Record.CaptureCount} 张 · 上滚 {run.Record.ScrollCount} 次 · 实际模型请求 {run.Record.ApiCallCount} 次";
        _thirdCandidates.Children.Clear();
        if (session is not null)
            for (var i = 0; i < session.Candidates.Count; i++)
            {
                var index = i;
                var button = Ui.Button((run!.SelectedIndex == i ? "✓ " : "") + $"选择候选 {i + 1}", () => ThirdSyncAction(() => ThirdWorkflow.Select(RequireThirdRun(), index)));
                button.IsEnabled = !busy && editable;
                var body = Ui.Text(session.Revisions[i].Greeting, 14); body.IsTextSelectionEnabled = true;
                _thirdCandidates.Children.Add(Ui.Card(Ui.Stack(8, button, body), run.SelectedIndex == i ? "E3F0E5" : "FFFFFF", 14));
            }
        _thirdCurrent!.Text = selected ? ThirdSelectedText() : generated ? "三个候选已生成，请明确选择其中一条。" : "生成后请先选择一条候选，再修改、复制或预填。";
        _thirdTurns!.Text = selected ? string.Join("\n\n", session!.Revisions[run!.SelectedIndex].Turns.Select((turn, i) =>
            $"第 {i + 1} 轮 · {turn.Instruction}\n{turn.Greeting}" + (turn.Note.Length > 0 ? "\n说明：" + turn.Note : ""))) : "";
        if (selected && _thirdTurns.Text.Length == 0) _thirdTurns.Text = "当前候选还没有修改记录。";
        _thirdState!.Text = run is null ? "尚未生成。" : run.Record.FillStatus + (run.Record.Error.Length > 0 ? " · " + run.Record.Error : "") +
            (run.IsReleased ? "\n本次采集检查已结束，仅可复制。再次预填请重新生成。" : $"\n预填检查最迟于 {run.ExpiresAt.ToLocalTime():HH:mm:ss} 结束；会话或草稿变化会提前停止。");
        _thirdResolve!.IsEnabled = _thirdNeutral!.IsEnabled = !busy && !generated;
        _thirdRegenerate!.IsEnabled = !busy && generated && editable;
        _thirdRetry!.IsEnabled = !busy && session is not null && !generated && (session.Extraction.Name is not null || session.Contact is { Confirmed: true });
        _thirdRevise!.IsEnabled = _thirdReset!.IsEnabled = !busy && selected && editable;
        _thirdCopy!.IsEnabled = !busy && selected;
        _thirdAppend!.IsEnabled = !busy && selected && run!.CanAppend;
    }

    private void StartThirdExpiryTimer()
    {
        _thirdExpiryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _thirdExpiryTimer.Tick -= ThirdExpiryTick;
        _thirdExpiryTimer.Tick += ThirdExpiryTick;
        _thirdExpiryTimer.Start();
    }

    private async void ThirdExpiryTick(object? sender, object e)
    {
        if (IsTaskBusy || _thirdRun is not { IsReleased: false } run) return;
        if (DateTimeOffset.UtcNow < run.ExpiresAt) return;
        _thirdExpiryTimer?.Stop();
        try { await ThirdWorkflow.ReleaseAsync(run); ThirdRenderHome(); }
        catch (Exception error) { Notify("结束本次会话检查失败：" + error.Message, InfoBarSeverity.Warning); }
    }

    private async Task ReleaseThirdRunAsync()
    {
        _thirdExpiryTimer?.Stop();
        if (_thirdRun is { } run && _thirdWorkflow is not null) await _thirdWorkflow.ReleaseAsync(run);
    }
}
