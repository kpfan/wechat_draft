using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;
using WechatDraft.Windows;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private WechatWindow? _preferredWechatWindow;
    private bool _updatingWindowChoices;

    private void ConfigureWindowRefresh()
    {
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) RefreshWindows();
        };
    }

    private void TrackWindowChoice(ComboBox selector)
    {
        selector.SelectionChanged += (_, _) =>
        {
            if (!_updatingWindowChoices)
            {
                if (_sentMonitor is not null && !Equals(_preferredWechatWindow, selector.SelectedItem))
                    StopSentRecording("目标微信窗口已切换，请重新开启发送归档。");
                _preferredWechatWindow = selector.SelectedItem as WechatWindow;
            }
        };
    }

    private void RefreshWindows()
    {
        if (_windows is null || !_homeVisible || _windowClosing ||
            _coordinator.IsRunning || _runCancellation is not null || _windows.IsDropDownOpen) return;
        try
        {
            // Enumerating native window/process metadata never captures a chat or activates WeChat.
            var found = WechatAdapter.FindWindows().OrderBy(window => window.ProcessId)
                .ThenBy(window => window.ProcessStartTicks).ThenBy(window => window.Handle.ToInt64()).ToArray();
            var previous = _windows.SelectedItem as WechatWindow ?? _preferredWechatWindow;
            _updatingWindowChoices = true;
            try
            {
                // Keep a stable list untouched so refresh does not flicker or reset selection.
                if (!_windows.Items.Cast<WechatWindow>().SequenceEqual(found))
                {
                    _windows.Items.Clear();
                    foreach (var window in found) _windows.Items.Add(window);
                }
                var selected = WechatWindowSelection.Select(_windows.Items.Cast<WechatWindow>().ToArray(), previous);
                _windows.SelectedItem = selected;
                _preferredWechatWindow = selected;
                _windows.PlaceholderText = found.Length == 0 ? "未找到微信窗口，打开微信后会自动识别" : "发现多个微信窗口，请选择目标窗口";
            }
            finally { _updatingWindowChoices = false; }
        }
        catch (Exception e)
        {
            _windows.PlaceholderText = "暂时无法获取微信窗口，稍后会自动重试";
            DebugSession.Current?.Event("window_discovery_failed", new { exceptionType = e.GetType().Name });
        }
    }
}
