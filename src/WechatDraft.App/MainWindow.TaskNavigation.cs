using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private ScrollViewer? _runningHomeView;
    private TaskProgress? _lastTaskProgress;
    private AppSettings? _homeSettingsAtTaskStart;
    private (string Text, InfoBarSeverity Severity)? _lastTaskNotice;
    private Border? _sidebarTask;
    private TextBlock? _sidebarTaskTitle;
    private TextBlock? _sidebarTaskDetail;
    private Button? _sidebarCancel;
    private bool _taskFinishedElsewhere;
    private readonly List<Button> _idleActions = [];
    private bool IsTaskBusy => _runCancellation is not null || _coordinator.IsRunning;

    private void Navigate(Action navigate)
    {
        if (_homeVisible && _scene is not null)
        {
            _settings = _settings with { Scene = _scene.Text.Trim(), ExtraInstruction = _extra?.Text.Trim() ?? "" };
            try { _repository.SaveSettings(_settings); }
            catch (Exception e) { Notify("页面内容尚未保存：" + e.Message, InfoBarSeverity.Warning); }
        }
        navigate();
    }

    private Border BuildSidebarTask()
    {
        _sidebarTaskTitle = Ui.Text("任务进行中", 12, "416A56", true);
        _sidebarTaskDetail = Ui.Text("", 11, "71817B");
        _sidebarTaskDetail.MaxLines = 2;
        var open = Ui.Button("查看", () => Navigate(ShowHome));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(open, "查看当前任务");
        _sidebarCancel = Ui.Button("取消", () => _runCancellation?.Cancel());
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_sidebarCancel, "取消当前任务");
        _sidebarTask = Ui.Card(Ui.Stack(6, _sidebarTaskTitle, _sidebarTaskDetail, Ui.Row(6, open, _sidebarCancel)), "EDF5EF", 10);
        _sidebarTask.Visibility = Visibility.Collapsed;
        return _sidebarTask;
    }

    private void RefreshSidebarTask()
    {
        if (_sidebarTask is null) return;
        var busy = IsTaskBusy;
        _sidebarTask.Visibility = busy || _taskFinishedElsewhere ? Visibility.Visible : Visibility.Collapsed;
        _sidebarTaskTitle!.Text = busy ? "任务进行中" : "任务已结束";
        _sidebarTaskDetail!.Text = _lastTaskProgress is { } progress
            ? busy ? progress.Stage + " · " + progress.Percent + "%" : progress.Stage + " · " + progress.Detail
            : "正在准备任务";
        _sidebarCancel!.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _sidebarCancel.IsEnabled = busy;
    }

    private Button IdleAction(string label, Action action)
    {
        var button = Ui.Button(label, () => { if (!IsTaskBusy) action(); });
        button.IsEnabled = !IsTaskBusy;
        _idleActions.Add(button);
        return button;
    }
}
