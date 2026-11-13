using System.Threading.Channels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;
using WechatDraft.Windows;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private WechatSentGreetingMonitor? _sentMonitor;
    private CancellationTokenSource? _sentSession;
    private readonly CancellationTokenSource _sentShutdown = new();
    private readonly Channel<(AppSettings Settings, SendObservation Observation, CancellationToken Token)> _sentQueue =
        Channel.CreateBounded<(AppSettings, SendObservation, CancellationToken)>(8);
    private Task? _sentWorker;
    private TextBlock? _sentStatus;
    private Button? _sentStartButton;
    private string _sentMessage = "尚未开启发送后归档。";
    private Action? _renderSentGreetings;
    private bool _sentStarting;

    private UIElement BuildSentRecordingControls()
    {
        _sentStatus = Ui.Text(_sentMessage, 13, "416A56");
        _sentStartButton = Ui.Button("开启当前聊天的发送归档", () => _ = StartSentRecordingAsync());
        return Ui.Card(Ui.Stack(12, Ui.Text("发送后自动保存祝福", 18, bold: true),
            Ui.Text("开启后，你在当前微信单聊手动发送的祝福会按实际文字保存，支持自己写和修改后的内容。无需先生成。", 13, "71817B"),
            _sentStatus, Ui.Row(8, _sentStartButton, Ui.Button("停止归档", () => StopSentRecording())),
            Ui.Text("仅记录开启时的聊天；换聊天或移动窗口后需重新开启。定位及标题 OCR 在本地完成；发送核对只上传前后两张标题与消息截图，可能产生模型费用。", 12, "8D713D")));
    }

    private void SentStatus(string message)
    {
        _sentMessage = message;
        if (_windowClosing) return;
        if (_sentStatus is not null) _sentStatus.Text = message;
        try { _renderSentGreetings?.Invoke(); }
        catch (Exception) { if (_sentStatus is not null) _sentStatus.Text = message + " 记录列表暂时无法读取，请稍后刷新。"; }
    }

    private async Task StartSentRecordingAsync()
    {
        if (_runCancellation is not null || _coordinator.IsRunning) return;
        RefreshWindows();
        if (_windows?.SelectedItem is not WechatWindow window)
        { Notify("请选择目标微信窗口。", InfoBarSeverity.Warning); return; }
        if (!await ConfirmScreenshotUploadAsync() || IsTaskBusy) return;
        StopSentRecording();
        _sentStarting = true;
        _runCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(_settings.TaskTimeoutSeconds, 10, 180)));
        SetBusy(true);
        SentStatus("正在定位当前聊天和发送按钮……");
        var configuration = _settings with { };
        try
        {
            _sentMonitor = await WechatSentGreetingMonitor.StartAsync(window.Handle, _runCancellation.Token);
            _sentSession = CancellationTokenSource.CreateLinkedTokenSource(_sentShutdown.Token);
            var sessionToken = _sentSession.Token;
            _sentMonitor.Observed += observation =>
            {
                if (!_sentQueue.Writer.TryWrite((configuration, observation, sessionToken)))
                {
                    try { SaveUnverifiedObservation(configuration, observation, "核对队列已满，本次未请求模型，请自行检查。"); }
                    finally { StopSentRecording("核对速度不足，归档已停止。待确认记录可在“已发送祝福”查看。"); }
                }
            };
            _sentMonitor.StatusChanged += SentStatus;
            _sentWorker ??= ProcessSentQueueAsync();
            SentStatus($"正在记录“{_sentMonitor.DisplayName}”中你发出的祝福。");
        }
        catch (Exception e) { StopSentRecording("未能开启归档：" + e.Message); }
        finally { _sentStarting = false; _runCancellation?.Dispose(); _runCancellation = null; SetBusy(false); }
    }

    private async Task ProcessSentQueueAsync()
    {
        var archiver = new SentGreetingArchiver(_repository, _protector, _client);
        try
        {
            await foreach (var item in _sentQueue.Reader.ReadAllAsync(_sentShutdown.Token))
            {
                try
                {
                    if (item.Token.IsCancellationRequested)
                    { SaveUnverifiedObservation(item.Settings, item.Observation, "记录已停止，本次核对未开始。"); continue; }
                    DispatcherQueue.TryEnqueue(() => SentStatus("正在核对实际发出的祝福……"));
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(item.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(item.Settings.TaskTimeoutSeconds, 10, 180)));
                    var record = await archiver.ProcessAsync(item.Settings, item.Observation, timeout.Token);
                    if (record is not null)
                        DispatcherQueue.TryEnqueue(() => SentStatus(record.Status == "已记录"
                            ? $"已保存发给“{record.DisplayName}”的祝福。" + (_sentMonitor?.IsRunning == true ? "继续监听当前聊天。" : "归档监听已停止。")
                            : record.Status == "无需归档" ? "本次未识别到新增祝福。" : "本次内容待确认，请在“已发送祝福”查看。"));
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    DispatcherQueue.TryEnqueue(() => StopSentRecording("发送核对或本地保存失败，归档已停止，请自行检查微信。"));
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SaveUnverifiedObservation(AppSettings settings, SendObservation observation, string reason)
    {
        if (_repository.FindSentGreeting(observation.Id) is not null) return;
        _repository.SaveSentGreeting(new SentGreetingRecord { Id = observation.Id, SessionId = observation.SessionId,
            AccountScope = settings.AccountScope, DisplayName = observation.DisplayName, ObservedAt = observation.ObservedAt,
            Trigger = observation.Trigger, Model = settings.Model, Status = "待确认", Detail = reason });
    }

    private void StopSentRecording(string message = "发送后归档已停止。")
    {
        if (_sentStarting) _runCancellation?.Cancel();
        var monitor = _sentMonitor; _sentMonitor = null;
        try { monitor?.Pause(true); }
        catch (Exception) { message += " 部分待确认信息未能保存。"; }
        finally { monitor?.Dispose(); }
        _sentSession?.Cancel(); _sentSession?.Dispose(); _sentSession = null;
        while (_sentQueue.Reader.TryRead(out var item))
        {
            try { SaveUnverifiedObservation(item.Settings, item.Observation, "记录已停止，本次核对未开始。"); }
            catch (Exception) { message += " 部分待确认信息未能保存。"; }
        }
        SentStatus(message);
    }

    private void CloseSentRecording()
    {
        StopSentRecording(); _sentShutdown.Cancel(); _sentQueue.Writer.TryComplete();
    }

    private void ShowSentGreetings()
    {
        SelectNavigation(nameof(ShowSentGreetings));
        _homeVisible = false;
        var search = Ui.Input("搜索已发送祝福", "", "联系人、实际正文或记录状态");
        var list = Ui.Stack(12); var status = Ui.Text(_sentMessage, 13, "71817B"); int loaded = 100;
        void Render()
        {
            list.Children.Clear(); status.Text = _sentMessage; var keyword = search.Text.Trim();
            var records = _repository.SentGreetings(_settings.AccountScope, loaded, includeDemo: false)
                .Where(x => x.Status != "无需归档" && (keyword.Length == 0 ||
                    (x.DisplayName + x.Text + x.Status).Contains(keyword, StringComparison.OrdinalIgnoreCase))).ToList();
            if (records.Count == 0) list.Children.Add(Ui.Text("还没有已发送祝福。开启归档后，发送的祝福会自动出现在这里。", 14, "71817B"));
            foreach (var record in records)
            {
                var body = Ui.Stack(10, Ui.Row(10, Ui.Text(record.DisplayName, 17, bold: true), Ui.Badge(record.Status)),
                    Ui.Text(record.Text.Length > 0 ? record.Text : "尚未取得可确认的完整正文。", 15),
                    Ui.Text($"观察时间：{record.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {record.Model} · 核对请求 {record.ApiCallCount} 次", 12, "7C8B81"),
                    Ui.Text(record.Detail, 12, "7C8B81"));
                var delete = Ui.Button("删除", async () =>
                {
                    if (await ConfirmAsync("删除这条发送记录？", "删除后无法在本应用中恢复。"))
                    { _repository.DeleteSentGreeting(record.Id); Render(); }
                });
                var correct = Ui.Button("核对 / 修正正文", async () =>
                {
                    var text = Ui.Input("实际发出的祝福", record.Text, "请从微信核对完整正文");
                    text.AcceptsReturn = true; text.TextWrapping = TextWrapping.Wrap; text.MaxLength = 8000; text.MinHeight = 140;
                    var dialog = new ContentDialog { Title = "核对发给“" + record.DisplayName + "”的祝福",
                        Content = Ui.Stack(12, Ui.Text("保存你人工核对的实际文字。", 13), text),
                        PrimaryButtonText = "保存", CloseButtonText = "取消", XamlRoot = _root.XamlRoot };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(text.Text))
                    {
                        var corrected = record with { Text = text.Text, Status = "人工确认", VerifiedAt = DateTimeOffset.UtcNow,
                            Detail = "正文经用户人工核对保存，不表示对方已收到或已读。" };
                        _repository.UpdateSentGreeting(corrected); Render();
                    }
                });
                correct.IsEnabled = record.Status != "核对中";
                body.Children.Add(Ui.Row(8, Ui.Button("复制实际正文", () => CopyText(record.Text)), correct, delete));
                list.Children.Add(Ui.Card(body));
            }
        }
        _renderSentGreetings = Render;
        search.TextChanged += (_, _) => Render();
        var clear = Ui.Button("清空当前工作区发送记录", async () =>
        {
            if (await ConfirmAsync("清空当前工作区的发送记录？", "归档监听会停止。生成历史和联系人档案保留。"))
            { StopSentRecording(); _repository.ClearSentGreetings(_settings.AccountScope); Render(); }
        });
        Render();
        Present(Ui.Stack(20, Ui.Text("已发送祝福", 28, bold: true),
            Ui.Text("保存聊天中实际发出的祝福，不要求先生成。状态依据本地画面核对，不代表对方收到或已读。", 13, "7C8B81"),
            status, search, Ui.Row(8, Ui.Button("刷新", Render), Ui.Button("停止归档", () => StopSentRecording())), list,
            Ui.Row(8, Ui.Button("加载更多", () => { loaded += 100; Render(); }), clear)));
    }
}
