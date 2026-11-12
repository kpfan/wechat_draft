using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private bool _updatingPrefill;

    private UIElement BuildPrefillOption()
    {
        _prefill = new CheckBox { Content = "允许选中候选后预填到微信输入框", IsChecked = _settings.EnableLiveAppend };
        _prefillHelp = Ui.Text(PrefillHint(), 12, "8D713D");
        _prefill.Checked += (_, _) => SavePrefillPreference();
        _prefill.Unchecked += (_, _) => SavePrefillPreference();
        return Ui.Stack(6, _prefill, _prefillHelp);
    }

    private string PrefillHint() => _settings.EnableLiveAppend
        ? "生成三个候选后，选择要使用的一条，再点“预填当前选中正文”。沿用开始时的聊天及完整草稿检查，最多保留 10 分钟；聊天、草稿或窗口变化后停止预填，不会自动发送。"
        : "只生成三个候选，可选择、修改和复制。开启预填仅对重新开始的任务生效。";

    private void SavePrefillPreference()
    {
        if (_updatingPrefill || _prefill is null || _coordinator.IsRunning || _runCancellation is not null) return;
        var previous = _settings.EnableLiveAppend;
        try
        {
            var next = _settings with { EnableLiveAppend = _prefill.IsChecked == true };
            _repository.SaveSettings(next);
            _settings = next;
        }
        catch (Exception e)
        {
            _updatingPrefill = true;
            _prefill.IsChecked = previous;
            _updatingPrefill = false;
            Notify("预填选项未能保存：" + e.Message, InfoBarSeverity.Warning);
        }
        if (_prefillHelp is not null) _prefillHelp.Text = PrefillHint();
        if (_startButton is not null) _startButton.Content = _settings.DemoMode ? "✦  生成并预填草稿" : "✦  生成三个祝福候选";
    }
}
