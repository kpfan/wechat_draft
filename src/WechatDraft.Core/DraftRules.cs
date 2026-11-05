using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WechatDraft.Core;

public static partial class DraftRules
{
    public static readonly string[] Relationships = ["family", "friend", "colleague", "manager", "teacher", "customer", "other", "unknown"];

    public static string AppendSuffix(string existing, string greeting) =>
        existing.Length == 0 || existing.EndsWith('\n') || existing.EndsWith('\r') ? greeting : "\r\n" + greeting;

    public static string Append(string existing, string greeting) => existing + AppendSuffix(existing, greeting);
    public static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    public static int VisibleLength(string text) => StringInfo.ParseCombiningCharacters(text).Length;

    public static void ValidateGreeting(ModelResponse response)
    {
        if (response.SchemaVersion != "1.0") throw new InvalidDataException("模型响应版本不受支持。");
        if (!Relationships.Contains(response.RelationshipType)) throw new InvalidDataException("模型返回了未知关系类型。");
        var text = response.GreetingText;
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("模型未返回祝福正文。");
        if (VisibleLength(text) > 120) throw new InvalidDataException("祝福超过 120 个可见字符，请缩短后重试。");
        if (text != text.Trim() || text.Contains('\n') || text.Contains('\r')) throw new InvalidDataException("祝福应为一段完整正文。");
        if (text.Any(char.IsControl) || text.Contains("```") || Placeholder().IsMatch(text))
            throw new InvalidDataException("祝福包含控制字符、格式标记或未替换变量。");
        if (response.AddressTerm?.Length > 30 || response.IdentityLabel?.Length > 80)
            throw new InvalidDataException("模型识别字段过长。");
    }

    public static void ValidateSceneGreeting(string greeting, string scene)
    {
        string[]? markers = scene.Trim() switch
        {
            "春节" => ["春节", "新春", "新年", "过年"],
            "元旦" => ["元旦", "新年"],
            "生日" => ["生日", "生辰", "新的一岁"],
            "中秋" => ["中秋", "月圆", "团圆"],
            "端午" => ["端午"], "国庆" => ["国庆"],
            "七夕" => ["七夕"], "圣诞" => ["圣诞"],
            "纪念日" => ["纪念", "周年"],
            _ => null
        };
        if (markers is not null && !markers.Any(greeting.Contains))
            throw new InvalidDataException("祝福没有体现所选场景，未写入微信。");
    }

    public static string? ResolveAddressTerm(Contact? contact, ModelResponse? response = null)
    {
        // A confirmed empty value is intentional; do not replace it with a model guess.
        var address = UsableAddressTerm(contact);
        if (address is null && contact?.Confirmed != true) address = response?.AddressTerm;
        return string.IsNullOrWhiteSpace(address) ? null : address.Trim();
    }

    public static string? UsableAddressTerm(Contact? contact)
    {
        if (contact is null || string.IsNullOrWhiteSpace(contact.AddressTerm)) return null;
        var address = contact.AddressTerm.Trim();
        return contact.Confirmed || contact.AddressTermSource == "user" ||
            (contact.AddressTermSource == "chat_message" && HasChatAddressEvidence(contact.AddressEvidence, address)) ? address : null;
    }

    public static Contact? ContactForGeneration(Contact? contact)
    {
        if (contact is null) return null;
        var address = UsableAddressTerm(contact);
        return contact with { AddressTerm = address,
            AddressTermSource = address is null ? "unknown" : contact.Confirmed ? "user" : contact.AddressTermSource,
            AddressEvidence = address is null ? null : contact.AddressEvidence };
    }

    public static bool HasChatAddressEvidence(AddressEvidence? evidence, string address, IReadOnlyList<Capture>? captures = null) =>
        evidence is { Source: "chat_message", Speaker: "self" } &&
        !string.IsNullOrWhiteSpace(evidence.CaptureId) && evidence.CaptureId.Length <= 120 &&
        !string.IsNullOrWhiteSpace(evidence.Quote) && evidence.Quote.Length <= 160 &&
        evidence.Quote.Contains(address, StringComparison.Ordinal) && evidence.Quote.Trim() != address &&
        (captures is null || captures.Any(capture => capture.Id == evidence.CaptureId));

    public static void ValidateSalutation(ModelResponse response, Contact? contact, IReadOnlyList<Capture> captures)
    {
        var address = ResolveAddressTerm(contact, response);
        if (address is null)
        {
            // Also reject obvious title-as-vocative output when the model leaves address_term empty.
            var names = new[] { contact?.DisplayName, contact?.RemarkName, response.ObservedDisplayName };
            if (names.Where(name => !string.IsNullOrWhiteSpace(name)).Any(name =>
                new[] { "，", ",", "：", ":", "您好", "你好" }.Any(separator => response.GreetingText.StartsWith(name!.Trim() + separator, StringComparison.Ordinal))))
                throw new InvalidDataException("正文疑似直接把标题、备注或昵称当作称呼，缺少聊天消息依据。");
            return;
        }
        if (address.Length > 30 || address.Any(char.IsControl) || Placeholder().IsMatch(address) || address.Contains("```"))
            throw new InvalidDataException("称呼格式无效，请在联系人档案中核对称呼。");
        if (UsableAddressTerm(contact) is null && !HasChatAddressEvidence(response.AddressEvidence, address, captures))
            throw new InvalidDataException("称呼缺少你在聊天中称呼对方的消息依据，不能直接使用标题、备注或昵称。");
        if (!response.GreetingText.StartsWith(address, StringComparison.Ordinal))
            throw new InvalidDataException("祝福正文未以对应称呼开头，请重新生成。");
    }

    public static Uri ValidateEndpoint(AppSettings settings)
    {
        if (!Uri.TryCreate(settings.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new InvalidDataException("服务地址无效，请使用不含凭据和查询参数的完整地址。");
        if (baseUri.Scheme != "https" && !(settings.AllowLoopbackHttp && baseUri.Scheme == "http" && baseUri.IsLoopback))
            throw new InvalidDataException("远程 API 需要 HTTPS；本机 HTTP 服务需单独启用。");
        if (string.IsNullOrWhiteSpace(settings.ApiPath) || Uri.TryCreate(settings.ApiPath, UriKind.Absolute, out _) ||
            settings.ApiPath.StartsWith('/') || settings.ApiPath.Contains("..") || settings.ApiPath.Contains('?') || settings.ApiPath.Contains('#'))
            throw new InvalidDataException("接口路径应为相对路径，例如 chat/completions。");
        return new Uri(baseUri, settings.ApiPath);
    }

    public static string Origin(AppSettings settings) => ValidateEndpoint(settings).GetLeftPart(UriPartial.Authority);

    public static ModelResponse ParseModel(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            if (firstLine >= 0 && text.EndsWith("```", StringComparison.Ordinal)) text = text[(firstLine + 1)..^3].Trim();
        }
        return JsonSerializer.Deserialize<ModelResponse>(text, JsonDefaults.Options)
            ?? throw new InvalidDataException("无法解析模型返回的 JSON。");
    }

    [GeneratedRegex(@"\{[^{}]*\}|\[[^\[\]]*(称呼|姓名|场景)[^\[\]]*\]")]
    private static partial Regex Placeholder();
}
