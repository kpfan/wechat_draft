using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;
using WechatDraft.Windows;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private void ShowApiProfiles() => RenderApiProfiles(null, false, null);

    private void ShowApiProfiles(string? message) => RenderApiProfiles(null, false, message);

    // The page is fully rebuilt on every action; editingId/adding describe the open editor.
    // Checking a radio selects that profile for the next task; editing saves without switching.
    private void RenderApiProfiles(string? editingId, bool adding, string? message, bool success = true)
    {
        SelectNavigation(nameof(ShowApiProfiles));
        _homeVisible = false;
        if (editingId is not null && !_settings.ApiProfiles.Any(p => p.Id == editingId))
        { editingId = null; message = "该 API 配置已不存在，请重新选择。"; success = false; }
        var editedProfile = editingId is null ? null : _settings.ApiProfiles.SingleOrDefault(p => p.Id == editingId);
        var formSettings = adding ? _settings with
        {
            BaseUrl = "", ApiPath = "chat/completions", Model = "", ProtectedApiKey = "",
            AuthorizedOrigin = "", VisionAuthorizedOrigin = "", AllowLoopbackHttp = false
        } : editedProfile is null ? null : ApiProfileSettings.EditForm(_settings, editedProfile.Id);
        var editorOpen = formSettings is not null;
        var editorDirty = false;
        var pageStatus = new InfoBar { IsOpen = false };
        void PageFeedback(string text, bool ok) { pageStatus.Message = text; pageStatus.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error; pageStatus.IsOpen = true; }
        if (message is not null) PageFeedback(message, success);
        async Task<bool> KeepEditsAsync()
            => !editorDirty || await ConfirmAsync("放弃未保存的修改？", "正在编辑的 API 内容尚未保存，继续会丢弃这些修改。");

        var rows = Ui.Stack(12);
        var emptyHint = Ui.Text("", 13, "839086");
        foreach (var profile in _settings.ApiProfiles)
        {
            var isActive = profile.Id == _settings.ActiveApiProfileId;
            var info = Ui.Stack(5,
                Ui.Text(profile.ApiProfileName + (isActive ? " · 使用中" : ""), 15, bold: true),
                Ui.Text("链接：" + profile.BaseUrl.TrimEnd('/') + "/" + profile.ApiPath, 12, "7C8B81"),
                Ui.Text("模型：" + (profile.Model.Length > 0 ? profile.Model : "尚未填写"), 12, "7C8B81"),
                Ui.Badge(profile.ProtectedApiKey.Length > 0 ? "密钥已加密保存" : "未保存密钥",
                    profile.ProtectedApiKey.Length > 0 ? "197861" : "8D794E",
                    profile.ProtectedApiKey.Length > 0 ? "E7F3ED" : "F5F0E4"));
            var choice = new RadioButton { Content = info, GroupName = "ApiProfiles", IsChecked = isActive };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(choice,
                profile.ApiProfileName + (isActive ? "，使用中" : "，勾选使用"));
            choice.Checked += async (_, _) =>
            {
                try
                {
                    if (!await KeepEditsAsync()) { RenderApiProfiles(editingId, adding, null); return; }
                    var candidate = ApiProfileSettings.Select(_settings, profile.Id);
                    StopSentRecording("模型 API 已切换，请为目标聊天重新开启发送归档。");
                    _repository.SaveSettings(candidate); _settings = candidate;
                    RenderApiProfiles(null, false, "当前使用：" + candidate.ApiProfileName + "。密钥已加密保护。");
                }
                catch (Exception e) { RenderApiProfiles(editingId, adding, e.Message, false); }
            };
            var edit = Ui.Button("编辑", async () =>
            {
                if (editingId == profile.Id) return;
                if (!await KeepEditsAsync()) return;
                RenderApiProfiles(profile.Id, false, null);
            });
            if (editingId == profile.Id) edit.IsEnabled = false;
            var delete = Ui.Button("删除", async () =>
            {
                var unsaved = editorDirty && editingId == profile.Id;
                if (!await ConfirmAsync("删除 API「" + profile.ApiProfileName + "」？",
                    (unsaved ? "该 API 正在编辑且修改尚未保存，删除会一并丢弃。" : "")
                    + "仅删除这组连接配置（含加密密钥与上传授权），历史记录保留。")) return;
                try
                {
                    var candidate = ApiProfileSettings.Delete(_settings, profile.Id);
                    _repository.SaveSettings(candidate); _settings = candidate;
                    RenderApiProfiles(null, false, "已删除 API「" + profile.ApiProfileName + "」。");
                }
                catch (Exception e) { PageFeedback(e.Message, false); }
            });
            rows.Children.Add(Ui.Card(Ui.Row(12, choice, edit, delete), "FFFFFF", 16));
        }
        if (_settings.ApiProfiles.Count == 0)
            emptyHint.Text = "还没有保存的 API 配置，点击「＋ 添加 API」创建第一组。";
        var add = Ui.Button("＋ 添加 API", async () =>
        {
            if (!await KeepEditsAsync()) return;
            RenderApiProfiles(null, true, null);
        }, true);
        var listCard = Ui.Card(Ui.Stack(12,
            Ui.Row(16, Ui.Text("已保存 " + _settings.ApiProfiles.Count + " 组 API", 20, bold: true), add),
            Ui.Text("勾选哪一组，生成任务就使用哪一组；点「编辑」可以修改链接、模型、密钥和授权。", 13, "7C8B81"),
            emptyHint, rows, pageStatus));

        Border? editorCard = null;
        if (editorOpen)
        {
            var editorStatus = new InfoBar { IsOpen = false };
            void EditorFeedback(string text, bool ok) { editorStatus.Message = text; editorStatus.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error; editorStatus.IsOpen = true; }
            var profileName = Ui.Input("API 配置名称", adding ? "" : editedProfile!.ApiProfileName, "例如：火山、备用 API");
            var baseUrl = Ui.Input("Base URL", formSettings!.BaseUrl, "https://服务域名/v1");
            var path = Ui.Input("接口相对路径", formSettings.ApiPath, "chat/completions");
            var model = Ui.Input("多模态模型名称", formSettings.Model, "填写服务商提供的视觉模型名称");
            var key = new PasswordBox { Header = "API Key", PlaceholderText = formSettings.ProtectedApiKey.Length > 0 ? "已加密保存；留空保留原密钥" : "只保存在当前 Windows 用户下", PasswordRevealMode = PasswordRevealMode.Peek };
            var local = new CheckBox { Content = "允许本机回环地址使用 HTTP", IsChecked = formSettings.AllowLoopbackHttp };
            var renderedOrigin = "";
            try { renderedOrigin = DraftRules.Origin(formSettings); } catch (InvalidDataException) { }
            var consent = new CheckBox { Content = "同意把必要聊天截图上传至上方服务地址", IsChecked = renderedOrigin.Length > 0 && formSettings.AuthorizedOrigin == renderedOrigin };
            var visionConsent = new CheckBox { Content = "允许视觉定位上传完整微信窗口（含联系人列表及已有草稿）", IsChecked = renderedOrigin.Length > 0 && formSettings.VisionAuthorizedOrigin == renderedOrigin };
            void EndpointChanged()
            {
                try
                {
                    var candidate = formSettings with { BaseUrl = baseUrl.Text, ApiPath = path.Text, AllowLoopbackHttp = local.IsChecked == true };
                    if (DraftRules.Origin(candidate) != renderedOrigin) { consent.IsChecked = false; visionConsent.IsChecked = false; }
                }
                catch { consent.IsChecked = false; visionConsent.IsChecked = false; }
            }
            baseUrl.TextChanged += (_, _) => EndpointChanged(); path.TextChanged += (_, _) => EndpointChanged();
            local.Click += (_, _) => EndpointChanged();
            AppSettings ReadForm()
            {
                var candidate = formSettings with { BaseUrl = baseUrl.Text.Trim().TrimEnd('/'), ApiPath = path.Text.Trim(), Model = model.Text.Trim(), AllowLoopbackHttp = local.IsChecked == true };
                var origin = DraftRules.Origin(candidate);
                candidate.AuthorizedOrigin = consent.IsChecked == true ? origin : "";
                candidate.VisionAuthorizedOrigin = consent.IsChecked == true && visionConsent.IsChecked == true ? origin : "";
                if (key.Password.Length > 0) candidate.ProtectedApiKey = _protector.Protect(key.Password);
                return candidate;
            }
            var editingActive = !adding && editingId == _settings.ActiveApiProfileId;
            var save = Ui.Button(adding ? "保存新 API" : "保存修改", () =>
            {
                try
                {
                    var savedName = profileName.Text.Trim();
                    var candidate = ApiProfileSettings.Save(ReadForm(), adding ? null : editingId, savedName, activate: false);
                    if (editingActive) StopSentRecording("模型 API 已更新，请为目标聊天重新开启发送归档。");
                    _repository.SaveSettings(candidate); _settings = candidate;
                    RenderApiProfiles(null, false, "已保存「" + savedName + "」" +
                        (editingActive ? "。当前使用的配置已同步更新，密钥已加密保护。" : "。在列表中勾选后即可使用。"));
                }
                catch (Exception e) { EditorFeedback(e.Message, false); }
            }, true);
            Button? test = null;
            test = Ui.Button("测试图像能力", async () =>
            {
                if (IsTaskBusy) { EditorFeedback("当前任务结束后再测试图像能力；配置可以先保存。", false); return; }
                test!.IsEnabled = false;
                try
                {
                    var candidate = ReadForm(); var (png, expected) = VisionTestImage.Create();
                    using var debug = DebugSession.Begin(candidate.DebugMode, Path.Combine(AppContext.BaseDirectory, "debug"), "vision-test");
                    EditorFeedback("正在发送不含个人信息的测试图……", true);
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(candidate.RequestTimeoutSeconds));
                    await _client.TestVisionAsync(candidate, _protector.Unprotect(candidate.ProtectedApiKey), png, expected, cancellation.Token);
                    EditorFeedback("通过：认证、图像识别和 JSON 响应可用。请保存。", true);
                }
                catch (Exception e) { EditorFeedback("测试失败：" + e.Message, false); }
                finally { test.IsEnabled = true; }
            });
            var clearKey = Ui.Button("清除密钥", async () =>
            {
                if (adding || editedProfile is null) return;
                if (!await ConfirmAsync("清除该 API 的密钥？", "清除后这组 API 需要重新输入密钥才能使用；其他 API 不受影响。")) return;
                try
                {
                    var candidate = ApiProfileSettings.Save(ApiProfileSettings.EditForm(_settings, editedProfile.Id) with { ProtectedApiKey = "" },
                        editedProfile.Id, editedProfile.ApiProfileName, activate: false);
                    if (editingActive) StopSentRecording("模型 API 已更新，请为目标聊天重新开启发送归档。");
                    _repository.SaveSettings(candidate); _settings = candidate;
                    RenderApiProfiles(editingId, false, "「" + editedProfile.ApiProfileName + "」的密钥已清除。");
                }
                catch (Exception e) { EditorFeedback(e.Message, false); }
            });
            if (adding) clearKey.Visibility = Visibility.Collapsed;
            var cancel = Ui.Button("取消编辑", async () =>
            {
                if (!await KeepEditsAsync()) return;
                RenderApiProfiles(null, false, null);
            });
            editorCard = Ui.Card(Ui.Stack(16,
                Ui.Text(adding ? "添加新的 API" : "编辑 API · " + editedProfile!.ApiProfileName, 20, bold: true),
                Ui.Badge("Chat Completions 图像协议"),
                profileName, baseUrl, path, model, key, local, consent, visionConsent,
                Ui.Row(10, save, test, clearKey, cancel), editorStatus,
                Ui.Text("测试仅上传自动生成的验证码图片，可能产生模型费用。不会上传微信截图。保存不会改变「使用中」的勾选。", 11, "8A978E")));
            foreach (var input in new[] { profileName, baseUrl, path, model }) input.TextChanged += (_, _) => editorDirty = true;
            key.PasswordChanged += (_, _) => editorDirty = true;
            foreach (var checkbox in new[] { local, consent, visionConsent }) checkbox.Click += (_, _) => editorDirty = true;
        }
        var content = Ui.Stack(20, Ui.Text("API 管理", 28, bold: true),
            Ui.Text("连接多组多模态模型服务。勾选哪一组，生成任务就使用哪一组；已保存的 API 可以随时修改链接和模型。", 13, "7C8B81"),
            listCard);
        if (editorCard is not null) content.Children.Add(editorCard);
        content.Children.Add(Ui.Text("切换或修改 API 不改变工作区、历史、场景与自动追加开关；新增配置不继承其他配置的密钥或授权。", 12, "8B958E"));
        Present(content);
    }
}
