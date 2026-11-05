using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;
using WechatDraft.Windows;
using Windows.Storage.Pickers;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private void ShowHistory()
    {
        SelectNavigation(nameof(ShowHistory));
        _homeVisible = false;
        var content = Ui.Stack(20, Ui.Text("历史记录", 28, bold: true), Ui.Text("保留每次生成的正文和当时的联系人信息。预填状态不代表已发送。", 13, "7C8B81"));
        var search = Ui.Input("搜索", "", "联系人、场景、正文或状态");
        var list = Ui.Stack(12); var count = Ui.Text("", 12, "859189"); int loaded = 100;
        void Render()
        {
            list.Children.Clear(); var keyword = search.Text.Trim();
            var records = _repository.History(_settings.AccountScope, loaded).Where(x => keyword.Length == 0 ||
                (x.DisplayName + x.Scene + x.Greeting + x.FillStatus).Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            count.Text = $"当前显示 {records.Count} 条 · 历史保留 {(_settings.RetentionDays == 0 ? "至手动清理" : _settings.RetentionDays + " 天")}";
            if (records.Count == 0) { list.Children.Add(Ui.Card(Ui.Stack(12, Ui.Text("还没有记录", 19, bold: true), Ui.Text("生成的每一句祝福，都会出现在这里。", 13, "8A978E")))); return; }
            foreach (var record in records)
            {
                var text = Ui.Text(record.Greeting.Length > 0 ? record.Greeting : record.Error, 15); text.IsTextSelectionEnabled = true;
                var metadata = $"{record.StartedAt.ToLocalTime():MM月dd日 HH:mm} · {record.Model} · 生成请求 {record.ApiCallCount} 次 · 布局请求 {record.LayoutApiCallCount} 次 · 上滚 {record.ScrollCount} 次";
                var header = Ui.Row(10, Ui.Text(record.DisplayName.Length > 0 ? record.DisplayName : "未识别联系人", 17, bold: true), Ui.Badge(record.Scene), Ui.Badge(record.FillStatus, "778278", "F0F3F0"));
                var actions = Ui.Row(8, Ui.Button("复制正文", () => CopyText(record.Greeting)), IdleAction("删除", async () =>
                { if (await ConfirmAsync("删除这条历史？", "删除后无法在本应用中恢复。") && !IsTaskBusy) { _repository.DeleteTask(record.Id); Render(); } }));
                if (record.DebugDirectory is { } debugPath)
                    actions.Children.Add(Ui.Button("查看调试记录", () =>
                    {
                        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "debug")) + Path.DirectorySeparatorChar;
                        var folder = Path.GetFullPath(debugPath);
                        if (folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder))
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                        else Notify("这条任务的调试目录不存在或不属于当前客户端。", InfoBarSeverity.Informational);
                    }));
                var body = Ui.Stack(12, header, text, Ui.Text(metadata, 11, "8E9991"),
                    Ui.Text($"关系：{RelationshipLabel(record.Relationship)}  ·  称呼：{record.AddressTerm ?? "未知"}  ·  身份：{record.IdentityLabel ?? "未知"}", 12, "7C8C81"));
                if (record.AddressTerm is not null) body.Children.Add(Ui.Text("称呼来源：" + AddressSourceLabel(record.AddressTermSource), 11, "7C8C81"));
                if (record.AddressEvidence is { } historyEvidence) body.Children.Add(Ui.Text("聊天依据（你发出的消息）：" + historyEvidence.Quote, 12, "7C8C81"));
                if (record.IsDemo) body.Children.Add(Ui.Badge("演示记录 · 虚构联系人", "847446", "F6F1E3"));
                if (record.ContactReused) body.Children.Add(Ui.Badge("已按备注复用联系人档案"));
                if (record.AutomationMethod is "uia" or "vision") body.Children.Add(Ui.Badge(record.AutomationMethod == "vision" ? "纯视觉" : "UIA"));
                if (record.FallbackReason.Length > 0) body.Children.Add(Ui.Text("视觉兜底原因：" + record.FallbackReason, 11, "8D7954"));
                if (record.OriginalTextRead == false) body.Children.Add(Ui.Badge("只采集截图 · 未读取草稿"));
                if (record.OriginalPreserved == true) body.Children.Add(Ui.Badge($"已核对原文保留 · {record.OriginalCharacters} 个字符"));
                if (record.Error.Length > 0) body.Children.Add(Ui.Text(record.Error, 12, "9D8050"));
                body.Children.Add(actions); list.Children.Add(Ui.Card(body));
            }
        }
        search.TextChanged += (_, _) => Render();
        content.Children.Add(search); content.Children.Add(count); content.Children.Add(list);
        content.Children.Add(Ui.Row(10, Ui.Button("加载更多", () => { loaded += 100; Render(); }), IdleAction("清空历史", async () =>
        { if (await ConfirmAsync("清空所有历史？", "这会删除演示和真实任务的全部历史，联系人档案仍可保留。") && !IsTaskBusy) { _repository.ClearHistory(); Render(); } })));
        Render(); Present(content);
    }

    private void ShowContacts()
    {
        SelectNavigation(nameof(ShowContacts));
        _homeVisible = false;
        var content = Ui.Stack(18, Ui.Text("联系人档案", 28, bold: true),
            Ui.Text("备注只用于匹配档案。称呼从聊天中你发给对方的消息推断，并保存消息依据；标题、备注和昵称不直接作为称呼。你也可以手动核对或修改。", 13, "7C8B81"));
        var search = Ui.Input("搜索联系人", "", "按备注或显示名查找"); var cards = Ui.Stack(12);
        void Render()
        {
            cards.Children.Clear();
            foreach (var contact in _repository.Contacts(_settings.AccountScope).Where(x =>
                (x.DisplayName + "\n" + x.RemarkName).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var card = Ui.Stack(12, Ui.Row(10, Ui.Text(contact.DisplayName, 18, bold: true), Ui.Badge(contact.Confirmed ? "信息已确认" : "模型候选"),
                    Ui.Badge(contact.IsDemo ? "演示" : "按备注匹配", "8D794E", "F5F0E4")),
                    Ui.Text($"匹配备注：{(string.IsNullOrWhiteSpace(contact.RemarkName) ? contact.DisplayName : contact.RemarkName)}", 12, "7C8B81"),
                    Ui.Text($"关系：{RelationshipLabel(contact.Relationship)}  ·  称呼：{contact.AddressTerm ?? "未知"}  ·  身份：{contact.IdentityLabel ?? "未知"}", 14, "738278"),
                    Ui.Text("称呼来源：" + AddressSourceLabel(contact.Confirmed ? "user" : contact.AddressTermSource), 12, "7C8B81"),
                    Ui.Row(8, IdleAction("编辑档案", () => _ = EditContactAsync(contact)), IdleAction("删除", async () =>
                    {
                        if (await ConfirmAsync("删除联系人档案？", "已有历史会保留生成时的信息快照。") && !IsTaskBusy) { _repository.DeleteContact(contact.Id, false); Render(); }
                    })));
                if (contact.AddressEvidence is { } evidence) card.Children.Insert(4, Ui.Text("聊天依据（你发出的消息）：" + evidence.Quote, 12, "7C8B81"));
                if (!string.IsNullOrWhiteSpace(contact.AddressTerm) && DraftRules.UsableAddressTerm(contact) is null)
                    card.Children.Insert(4, Ui.Text("旧称呼没有可靠来源，暂不用于生成；等待聊天重新识别或你确认。", 12, "8D7954"));
                cards.Children.Add(Ui.Card(card));
            }
            if (cards.Children.Count == 0) cards.Children.Add(Ui.Card(Ui.Text("还没有匹配的联系人。", 15, "839086")));
        }
        search.TextChanged += (_, _) => Render(); content.Children.Add(search);
        content.Children.Add(IdleAction("新增联系人档案", () => _ = EditContactAsync(new Contact { AccountScope = _settings.AccountScope })));
        content.Children.Add(cards); Render(); Present(content);
    }

    private async Task EditContactAsync(Contact contact)
    {
        if (IsTaskBusy) return;
        var name = Ui.Input("显示名", contact.DisplayName); var remark = Ui.Input("备注名（可空）", contact.RemarkName ?? "");
        var relationship = Ui.Combo("与我的关系", DraftRules.Relationships.Select(RelationshipLabel), RelationshipLabel(contact.Relationship));
        var address = Ui.Input("称呼（用于祝福开头）", contact.AddressTerm ?? "", "例如：陈老师、小王；不确定时留空");
        var identity = Ui.Input("补充身份（可空）", contact.IdentityLabel ?? "", "例如：前同事");
        var confirmed = new CheckBox { Content = "我已核对上述关系和称呼", IsChecked = contact.Confirmed };
        var form = Ui.Stack(14, name, remark, relationship, address, identity, confirmed,
            Ui.Text("自动称呼来自你发给对方的聊天消息，备注或昵称只用于匹配。修改称呼会标记为你填写；勾选确认后，模型不会覆盖已确认字段。", 12, "8B978F"));
        var dialog = new ContentDialog { Title = "联系人档案", Content = form, PrimaryButtonText = "保存", CloseButtonText = "取消", XamlRoot = _root.XamlRoot };
        dialog.PrimaryButtonClick += (_, args) => { if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Length > 80 || address.Text.Length > 30) args.Cancel = true; };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || IsTaskBusy) return;
        contact.DisplayName = name.Text.Trim(); contact.RemarkName = NullIfEmpty(remark.Text);
        contact.Relationship = DraftRules.Relationships.First(x => RelationshipLabel(x) == (string)relationship.SelectedItem);
        var editedAddress = NullIfEmpty(address.Text);
        if (editedAddress != contact.AddressTerm)
        {
            contact.AddressTermSource = editedAddress is null ? "unknown" : "user";
            contact.AddressEvidence = null;
        }
        contact.AddressTerm = editedAddress; contact.IdentityLabel = NullIfEmpty(identity.Text); contact.Confirmed = confirmed.IsChecked == true;
        _repository.SaveContact(contact); SeedDemoContacts(); ShowContacts();
    }

    private static string AddressSourceLabel(string source) => source switch
    {
        "chat_message" => "聊天中你对对方的称呼（模型候选）",
        "user" => "你填写或确认",
        _ => "未知／待核对"
    };

    private void ShowSettings() => ShowSettings(null);

    private void ShowSettings(string? message)
    {
        SelectNavigation(nameof(ShowSettings));
        _homeVisible = false;
        var dirty = false;
        var automation = Ui.Combo("微信识别方式", ["自动 · UIA 优先", "仅 UIA", "仅视觉"], AutomationLabel(_settings.AutomationMode));
        var liveAppend = new CheckBox { Content = "生成后预填到微信输入框（与首页选项同步）", IsChecked = _settings.EnableLiveAppend };
        var debugMode = new CheckBox { Content = "调试模式：保存中间截图和模型日志", IsChecked = DebugSession.IsSupported && _settings.DebugMode,
            IsEnabled = DebugSession.IsSupported };
        var account = Ui.Input("本地微信账号工作区", _settings.AccountScope, "更换微信账号时请更改工作区名称");
        var retention = Ui.Combo("历史保留期限", ["30 天", "90 天", "手动清理"], _settings.RetentionDays == 0 ? "手动清理" : _settings.RetentionDays + " 天");
        var hotkey = Ui.Input("快捷键字母（Ctrl + Alt + 字母）", ((char)_settings.HotkeyVirtualKey).ToString()); hotkey.MaxLength = 1;
        var status = new InfoBar { IsOpen = false };
        void Feedback(string text, bool ok) { status.Message = text; status.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error; status.IsOpen = true; }
        if (message is not null) Feedback(message, true);
        AppSettings ReadForm()
        {
            var candidate = _settings with
            {
                EnableLiveAppend = liveAppend.IsChecked == true,
                DebugMode = DebugSession.IsSupported && debugMode.IsChecked == true,
                AutomationMode = (string)automation.SelectedItem == "仅 UIA" ? "uia" : (string)automation.SelectedItem == "仅视觉" ? "vision" : "auto",
                AccountScope = account.Text.Trim(),
                RetentionDays = (string)retention.SelectedItem == "手动清理" ? 0 : (string)retention.SelectedItem == "30 天" ? 30 : 90
            };
            if (string.IsNullOrWhiteSpace(candidate.AccountScope)) throw new InvalidDataException("账号工作区不能为空。");
            var letter = hotkey.Text.ToUpperInvariant(); if (letter.Length != 1 || letter[0] is < 'A' or > 'Z') throw new InvalidDataException("快捷键字母应为 A 至 Z。");
            candidate.HotkeyVirtualKey = letter[0];
            return candidate;
        }
        var save = Ui.Button("保存设置", () =>
        {
            try
            {
                var candidate = ReadForm();
                if (!_paused && _hotkey?.Register(candidate.HotkeyVirtualKey) == false) { _hotkey.Register(_settings.HotkeyVirtualKey); throw new InvalidDataException("快捷键被占用，请更换字母。"); }
                StopSentRecording("设置已更新，请为目标聊天重新开启发送归档。");
                _repository.SaveSettings(candidate); _settings = candidate; _repository.Cleanup(candidate.RetentionDays); SeedDemoContacts();
                _footer.Text = "Ctrl + Alt + " + (char)candidate.HotkeyVirtualKey;
                ShowSettings("设置已保存。模型 API 的连接与授权在「API 管理」页面维护。");
            }
            catch (Exception e) { Feedback(e.Message, false); }
        }, true);
        var options = Ui.Card(Ui.Stack(17, Ui.Text("工作区与预填", 20, bold: true), automation, account, hotkey, retention, liveAppend,
            Ui.Text("预填默认关闭，与首页选项同步。生成期间可以浏览本软件，填写前最多等待 30 秒返回原微信聊天，不抢占窗口。转到其他程序、切换聊天、修改草稿或输入焦点不安全时不填写。复查当前聊天面板、标题、输入控件和原草稿；支持的界面不要求左侧联系人一直可见。不会自动发送。", 12, "8B7953"),
            Ui.Text("仅视觉或自动回退到视觉时，只使用固定画面生成，不自动填写；取得画面后可移动鼠标或切换窗口。UIA 采集截图和实际预填时须在原微信聊天前台，已有文字会保留并在末尾追加。", 12, "8B7953"),
            debugMode,
            Ui.Text(DebugSession.IsSupported ? "调试模式会在程序旁的 debug 文件夹按任务保存截图、模型原始返回、定位结果和日志，可能含联系人、聊天及草稿。关闭后不再新增，已有文件保留。密钥始终不写入日志。" : "当前为正式版本，调试记录已在构建时禁用，不保存中间截图和模型日志。", 12, "7F8D82"),
            Ui.Button("打开调试文件夹", () =>
            {
                var folder = Path.Combine(AppContext.BaseDirectory, "debug");
                if (!Directory.Exists(folder)) { Feedback("尚无调试记录。开启调试模式、保存设置并执行任务后生成。", true); return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }),
            Ui.Text("常规业务数据位于程序旁的 data 文件夹；关闭调试模式时截图仅在内存处理。", 12, "7F8D82"),
            save, status));
        foreach (var input in new[] { account, hotkey }) input.TextChanged += (_, _) => dirty = true;
        foreach (var checkbox in new[] { liveAppend, debugMode }) checkbox.Click += (_, _) => dirty = true;
        automation.SelectionChanged += (_, _) => dirty = true; retention.SelectionChanged += (_, _) => dirty = true;
        Present(Ui.Stack(20, Ui.Text("设置", 28, bold: true),
            Ui.Text("控制工作区、快捷键与调试记录；模型服务的连接、密钥与授权已移至「API 管理」页面。", 13, "7C8B81"),
            Ui.Button("前往 API 管理", async () =>
            { if (dirty && !await ConfirmAsync("放弃未保存的修改？", "设置页的修改尚未保存，继续会丢弃这些修改。")) return; ShowApiProfiles(); }),
            options));
    }
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string RelationshipLabel(string relationship) => relationship switch
    { "family" => "家人", "friend" => "朋友", "colleague" => "同事", "manager" => "领导", "teacher" => "老师", "customer" => "客户", "other" => "其他", _ => "未知" };
}
