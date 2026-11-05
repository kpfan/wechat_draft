using System.Windows.Input;
using Microsoft.UI.Xaml;
using WechatDraft.Core;

namespace WechatDraft.App;

public sealed class HistoryItem
{
    public string DisplayName { get; }
    public string Scene { get; }
    public string FillStatus { get; }
    public string Body { get; }
    public string Metadata { get; }
    public string Details { get; }
    public bool IsExpanded { get; set; }
    public Visibility DebugVisibility { get; }
    public ICommand CopyCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DebugCommand { get; }

    internal HistoryItem(TaskRecord record, Action copy, Action delete, Action debug, Func<bool> canDelete)
    {
        DisplayName = record.DisplayName.Length > 0 ? record.DisplayName : "未识别联系人";
        Scene = record.Scene; FillStatus = record.FillStatus;
        Body = record.Greeting.Length > 0 ? record.Greeting : record.Error;
        Metadata = $"{record.StartedAt.ToLocalTime():MM月dd日 HH:mm} · {record.Model} · 生成请求 {record.ApiCallCount} 次 · 布局请求 {record.LayoutApiCallCount} 次 · 上滚 {record.ScrollCount} 次";
        var details = new List<string>
        {
            $"关系：{MainWindow.RelationshipLabel(record.Relationship)}  ·  称呼：{record.AddressTerm ?? "未知"}  ·  身份：{record.IdentityLabel ?? "未知"}"
        };
        if (record.AddressTerm is not null) details.Add("称呼来源：" + MainWindow.AddressSourceLabel(record.AddressTermSource));
        if (record.AddressEvidence is { } evidence) details.Add("聊天依据（你发出的消息）：" + evidence.Quote);
        if (record.IsDemo) details.Add("演示记录 · 虚构联系人");
        if (record.ContactReused) details.Add("已按备注复用联系人档案");
        if (record.AutomationMethod is "uia" or "vision") details.Add(record.AutomationMethod == "vision" ? "纯视觉" : "UIA");
        if (record.FallbackReason.Length > 0) details.Add("视觉兜底原因：" + record.FallbackReason);
        if (record.OriginalTextRead == false) details.Add("只采集截图 · 未读取草稿");
        if (record.OriginalPreserved == true) details.Add($"已核对原文保留 · {record.OriginalCharacters} 个字符");
        if (record.Error.Length > 0 && record.Greeting.Length > 0) details.Add(record.Error);
        Details = string.Join(Environment.NewLine, details);
        DebugVisibility = record.DebugDirectory is null ? Visibility.Collapsed : Visibility.Visible;
        CopyCommand = new HistoryAction(copy, () => record.Greeting.Length > 0);
        DeleteCommand = new HistoryAction(delete, canDelete);
        DebugCommand = new HistoryAction(debug);
    }

    internal void RefreshActions() => ((HistoryAction)DeleteCommand).Refresh();

    private sealed class HistoryAction(Action action, Func<bool>? enabled = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
        public void Execute(object? parameter) { if (CanExecute(parameter)) action(); }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
