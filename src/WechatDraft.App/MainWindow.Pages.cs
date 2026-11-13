using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using WechatDraft.Core;
using WechatDraft.Windows;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private void ShowHistory()
    {
        SelectNavigation(nameof(ShowHistory));
        _homeVisible = false;
        _historyView = new HistoryPageView(_repository, _settings.AccountScope, _settings.RetentionDays,
            () => !IsTaskBusy, ConfirmAsync, CopyText);
        _historyView.SetTaskBusy(IsTaskBusy);
        _page.Content = _historyView;
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
                !x.IsDemo && (x.DisplayName + "\n" + x.RemarkName).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var card = Ui.Stack(12, Ui.Row(10, Ui.Text(contact.DisplayName, 18, bold: true), Ui.Badge(contact.Confirmed ? "信息已确认" : "模型候选"),
                    Ui.Badge("按备注匹配", "8D794E", "F5F0E4")),
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
        var address = Ui.Input("称呼（用于祝福开头）", contact.AddressTerm ?? "", "例如：小明。不确定时留空。");
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
        _repository.SaveContact(contact); ShowContacts();
    }

    internal static string AddressSourceLabel(string source) => source switch
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
        var shortcut = _settings.GenerationHotkey;
        var shortcutLabel = Ui.Text(shortcut.ToString(), bold: true);
        shortcutLabel.VerticalAlignment = VerticalAlignment.Center;
        var hotkeyHelp = Ui.Text("点击录制，在弹窗里按下你想用的组合键。", 12, "7F8D82");
        var recordHotkey = Ui.Button("录制快捷键", async () =>
        {
            if (await RecordHotkeyAsync() is not { } value) return;
            dirty |= shortcut != value;
            shortcut = value;
            shortcutLabel.Text = value.ToString();
            hotkeyHelp.Text = "录制完成。点击「保存设置」使快捷键生效。";
        });
        var hotkey = Ui.Stack(6, Ui.Text("全局快捷键"), Ui.Row(12, recordHotkey, shortcutLabel), hotkeyHelp);
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
            if (!shortcut.IsValid)
                throw new InvalidDataException("请录制一个有效的快捷键。");
            candidate.HotkeyVirtualKey = shortcut.VirtualKey;
            candidate.HotkeyModifiers = shortcut.Modifiers;
            return candidate;
        }
        var save = Ui.Button("保存设置", () =>
        {
            try
            {
                var candidate = ReadForm();
                if (_paused || _hotkey is null)
                    _repository.SaveSettings(candidate);
                else if (!_hotkey.Register(candidate.GenerationHotkey, () => _repository.SaveSettings(candidate)))
                    throw new InvalidDataException("快捷键已被占用或由 Windows 保留，请换一个组合。");
                _settings = candidate;
                UpdateHotkeyFooter();
                StopSentRecording("设置已更新，请为目标聊天重新开启发送归档。");
                _repository.Cleanup(candidate.RetentionDays);
                ShowSettings("设置已保存。模型 API 的连接与授权在「API 管理」页面维护。");
            }
            catch (Exception e) { Feedback(e.Message, false); }
        }, true);
        var options = Ui.Card(Ui.Stack(17, Ui.Text("工作区与预填", 20, bold: true), automation, account, hotkey, retention, liveAppend,
            Ui.Text("预填与首页选项同步。第三版生成后先明确选中候选，再点击预填；十分钟后只能复制。填写前最多等待 30 秒返回原微信聊天，不抢占窗口。视觉模式追加到填写时的当前完整草稿，UIA 要求原稿不变；均由原适配器复查目标和输入区。强制生成仅供复制，不会自动发送。", 12, "8B7953"),
            Ui.Text("视觉模式用本地 OCR 读取并复查标题。开启预填后，会核对完整原文、追加并读回确认；复制或标题不明确时只保留生成结果。生成时可查看助手，实际读取和填写须回到原微信聊天。不会自动发送。", 12, "8B7953"),
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
        account.TextChanged += (_, _) => dirty = true;
        foreach (var checkbox in new[] { liveAppend, debugMode }) checkbox.Click += (_, _) => dirty = true;
        automation.SelectionChanged += (_, _) => dirty = true; retention.SelectionChanged += (_, _) => dirty = true;
        Present(Ui.Stack(20, Ui.Text("设置", 28, bold: true),
            Ui.Text("控制工作区、快捷键与调试记录；模型服务的连接、密钥与授权已移至「API 管理」页面。", 13, "7C8B81"),
            Ui.Button("前往 API 管理", async () =>
            { if (dirty && !await ConfirmAsync("放弃未保存的修改？", "设置页的修改尚未保存，继续会丢弃这些修改。")) return; ShowApiProfiles(); }),
            options));
    }
    private async Task<HotkeyShortcut?> RecordHotkeyAsync()
    {
        var hint = Ui.Text("同时按下你想使用的组合，例如 Ctrl + Shift + B、Alt + 7 或 F9。", 13, "7F8D82");
        var dialog = new ContentDialog
        {
            Title = "请按下快捷键",
            Content = Ui.Stack(12, Ui.Badge("等待按键"), hint),
            CloseButtonText = "取消",
            RequestedTheme = _root.RequestedTheme,
            XamlRoot = _root.XamlRoot
        };
        HotkeyShortcut? result = null;
        var closing = false;
        void FinishRecording(HotkeyShortcut? recorded)
        {
            if (closing) return;
            closing = true;
            result = recorded;
            dialog.Hide();
        }
        dialog.Closing += (_, _) => closing = true;
        dialog.PreviewKeyDown += (_, args) =>
        {
            if (closing || args.Key is VirtualKey.Tab or VirtualKey.Escape or VirtualKey.Enter or VirtualKey.Space) return;
            args.Handled = true;
            if (args.KeyStatus.WasKeyDown || args.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or
                VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.LeftMenu or VirtualKey.RightMenu or
                VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;

            var recorded = new HotkeyShortcut(ReadHotkeyModifiers(), (uint)args.OriginalKey);
            if (recorded.IsValid) FinishRecording(recorded);
            else hint.Text = "请按修饰键加字母或数字，或按 F1–F11。按 Esc 取消。";
        };
        _finishHotkeyRecording = FinishRecording;
        try
        {
            await dialog.ShowAsync();
            return result;
        }
        finally
        {
            _finishHotkeyRecording = null;
        }
    }

    private static HotkeyModifiers ReadHotkeyModifiers()
    {
        static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        var modifiers = HotkeyModifiers.None;
        if (Down(VirtualKey.Control)) modifiers |= HotkeyModifiers.Control;
        if (Down(VirtualKey.Menu)) modifiers |= HotkeyModifiers.Alt;
        if (Down(VirtualKey.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= HotkeyModifiers.Windows;
        return modifiers;
    }
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    internal static string RelationshipLabel(string relationship) => relationship switch
    { "family" => "家人", "friend" => "朋友", "colleague" => "同事", "manager" => "领导", "teacher" => "老师", "customer" => "客户", "other" => "其他", _ => "未知" };
}
