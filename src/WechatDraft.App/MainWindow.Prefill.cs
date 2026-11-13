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
        ? "选中候选后点击预填，再回到原微信聊天，最多等待 30 秒。视觉模式追加到填写时的当前草稿末尾，UIA 模式要求原稿保持一致。强制生成仅供复制；发送始终手动确认。"
        : "只生成祝福，结果显示在助手中。开启后仅对新任务生效。";

    private void SavePrefillPreference()
    {
        if (_updatingPrefill || _prefill is null || IsTaskBusy) return;
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
        if (_startButton is not null) _startButton.Content = "✦  生成三个祝福候选";
    }
}
