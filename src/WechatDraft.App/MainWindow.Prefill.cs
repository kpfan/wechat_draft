using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private bool _updatingPrefill;

    private UIElement BuildPrefillOption()
    {
        _prefill = new CheckBox { Content = "生成后预填到微信输入框", IsChecked = _settings.EnableLiveAppend };
        _prefillHelp = Ui.Text(PrefillHint(), 12, "8D713D");
        _prefill.Checked += (_, _) => SavePrefillPreference();
        _prefill.Unchecked += (_, _) => SavePrefillPreference();
        return Ui.Stack(6, _prefill, _prefillHelp);
    }

    private string PrefillHint() => _settings.EnableLiveAppend
        ? "生成期间可以浏览本软件。填写时需回到原微信聊天，最多等待 30 秒，不抢占窗口；切换聊天、修改微信草稿或转到其他程序会停止预填。同一聊天且草稿未变化后在末尾追加，不会自动发送。"
        : "只生成祝福，结果显示在助手中。开启后仅对新任务生效。";

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
        if (_startButton is not null) _startButton.Content = _settings.EnableLiveAppend ? "✦  生成并预填草稿" : "✦  生成祝福";
    }
}
