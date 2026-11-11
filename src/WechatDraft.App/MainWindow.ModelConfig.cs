using Microsoft.UI.Xaml.Controls;
using WechatDraft.Core;

namespace WechatDraft.App;

public sealed partial class MainWindow
{
    private bool _modelConsentOpen;

    private string? LoadPackagedDefault()
    {
        try
        {
            var candidate = ModelConfigPackage.LoadDefault(_settings, AppContext.BaseDirectory, _protector);
            if (!ReferenceEquals(candidate, _settings))
            {
                _repository.SaveSettings(candidate);
                _settings = candidate;
            }
            return null;
        }
        catch (InvalidDataException e)
        {
            return e.Message;
        }
        catch (Exception)
        {
            return "无法读取或保存加密模型配置。请检查配置文件和软件包。";
        }
    }

    private async Task<bool> ConfirmScreenshotUploadAsync()
    {
        if (_modelConsentOpen) return false;
        var settings = _settings;
        if (string.IsNullOrWhiteSpace(settings.Model) || string.IsNullOrWhiteSpace(settings.ProtectedApiKey))
        {
            Notify("请在「API 管理」添加并勾选已配置模型名称和 API Key 的 API。", InfoBarSeverity.Warning);
            return false;
        }

        try
        {
            var origin = DraftRules.Origin(settings);
            if (settings.AuthorizedOrigin == origin) return true;
            _modelConsentOpen = true;
            Activate();
            if (!await ConfirmAsync("允许当前模型处理聊天截图？",
                "生成祝福或核对已发送的祝福时，必要的标题和聊天消息截图会上传给当前模型分析。")) return false;
            if (_windowClosing) return false;
            var candidate = ApiProfileSettings.AuthorizeActiveProfile(_settings, settings.ActiveApiProfileId, origin);
            _repository.SaveSettings(candidate);
            _settings = candidate;
            return true;
        }
        catch (InvalidDataException e)
        {
            Notify(e.Message, InfoBarSeverity.Warning);
            return false;
        }
        catch (Exception)
        {
            Notify("未能保存当前模型的截图授权，请重试。", InfoBarSeverity.Warning);
            return false;
        }
        finally { _modelConsentOpen = false; }
    }
}
