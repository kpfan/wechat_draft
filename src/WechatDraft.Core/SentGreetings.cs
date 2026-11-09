using System.Text.Json.Serialization;

namespace WechatDraft.Core;

public sealed record SendObservation(string Id, string SessionId, string DisplayName,
    DateTimeOffset ObservedAt, string Trigger, Capture Before, Capture After, string LocalUncertainty = "");

public sealed record SentGreetingRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = "";
    public string AccountScope { get; init; } = "default";
    public string? ContactId { get; set; }
    public string DisplayName { get; init; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? VerifiedAt { get; set; }
    public string Status { get; set; } = "核对中";
    public string Detail { get; set; } = "";
    public string Trigger { get; init; } = "";
    public string Model { get; init; } = "";
    public int ApiCallCount { get; set; }
    public bool IsDemo { get; init; }
}

public sealed record SentGreetingResponse
{
    [JsonPropertyName("conversation_type")] public string ConversationType { get; init; } = "unknown";
    [JsonRequired, JsonPropertyName("schema_version")] public string SchemaVersion { get; init; } = "";
    [JsonRequired, JsonPropertyName("before_title")] public string BeforeTitle { get; init; } = "";
    [JsonRequired, JsonPropertyName("after_title")] public string AfterTitle { get; init; } = "";
    [JsonRequired, JsonPropertyName("new_outgoing_count")] public int NewOutgoingCount { get; init; }
    [JsonRequired, JsonPropertyName("is_blessing")] public bool IsBlessing { get; init; }
    [JsonRequired, JsonPropertyName("text_complete")] public bool TextComplete { get; init; }
    [JsonRequired, JsonPropertyName("uncertain")] public bool Uncertain { get; init; } = true;
    [JsonRequired, JsonPropertyName("send_failed")] public bool SendFailed { get; init; }
    [JsonRequired, JsonPropertyName("message_text")] public string MessageText { get; init; } = "";
}

public interface ISentGreetingModel
{
    Task<SentGreetingResponse> ReadSentGreetingAsync(AppSettings settings, string key,
        SendObservation observation, CancellationToken cancellationToken);
}

// A send gesture is only a hint. A complete, unambiguous new outgoing blessing is required.
public sealed class SentGreetingArchiver(LocalRepository repository, ISecretProtector protector, ISentGreetingModel model)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SentGreetingRecord?> ProcessAsync(AppSettings settings, SendObservation observation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var existing = repository.FindSentGreeting(observation.Id);
            if (existing is not null)
            {
                if (existing.AccountScope != settings.AccountScope) throw new InvalidDataException("发送观察属于其他账号工作区。");
                return existing; // Never replay an uncertain observation.
            }
            if (settings.DemoMode || settings.AuthorizedOrigin != DraftRules.Origin(settings))
                throw new InvalidDataException("请先授权当前服务读取聊天截图。");
            if (string.IsNullOrWhiteSpace(observation.DisplayName) || observation.Before.PngBytes.Length == 0 ||
                observation.After.PngBytes.Length == 0 || (long)observation.Before.PngBytes.Length + observation.After.PngBytes.Length > 12 * 1024 * 1024)
                throw new InvalidDataException("发送观察的标题或截图无效。");
            if (observation.LocalUncertainty.Length == 0 && observation.Before.PngBytes.AsSpan().SequenceEqual(observation.After.PngBytes)) return null;
            var record = new SentGreetingRecord
            {
                Id = observation.Id, SessionId = observation.SessionId, AccountScope = settings.AccountScope,
                DisplayName = observation.DisplayName, ObservedAt = observation.ObservedAt, Trigger = observation.Trigger,
                Model = settings.Model
            };
            repository.SaveSentGreeting(record);
            if (observation.LocalUncertainty.Length > 0)
            {
                record.Status = "待确认"; record.Detail = observation.LocalUncertainty;
                repository.SaveSentGreeting(record); return record;
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                record.ApiCallCount = 1;
                var answer = await model.ReadSentGreetingAsync(settings, protector.Unprotect(settings.ProtectedApiKey), observation, ct);
                if ((observation.Before.RequiresConversationCheck || observation.After.RequiresConversationCheck) &&
                    answer.ConversationType != "single")
                    throw new InvalidDataException("截图未能确认是无遮挡的普通单聊，未自动归档正文。");
                if (answer.SchemaVersion != "1.0" || answer.BeforeTitle.Trim() != observation.DisplayName.Trim() ||
                    answer.AfterTitle.Trim() != observation.DisplayName.Trim())
                    throw new InvalidDataException("发送前后聊天标题不一致，未自动归档正文。");
                if (answer.NewOutgoingCount < 0 || answer.NewOutgoingCount > 100)
                    throw new InvalidDataException("模型返回的新增消息数量无效。");
                if (answer.Uncertain || answer.SendFailed || answer.NewOutgoingCount > 1)
                    throw new InvalidDataException("消息方向、发送状态或多条消息无法明确核对，请自行确认。");
                if (answer.NewOutgoingCount == 0 || !answer.IsBlessing)
                {
                    record.Status = "无需归档"; record.Detail = "未发现一条新增的自己发出的祝福。";
                }
                else
                {
                    if (!answer.TextComplete || string.IsNullOrWhiteSpace(answer.MessageText) || answer.MessageText.Length > 8000 ||
                        answer.MessageText.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
                        throw new InvalidDataException("祝福文字未完整读取，未自动归档正文。");
                    ct.ThrowIfCancellationRequested();
                    if (repository.FindSentGreeting(record.Id) is null) return null;
                    var contact = repository.FindContactByName(settings.AccountScope, observation.DisplayName);
                    if (contact is null)
                    {
                        contact = new Contact { AccountScope = settings.AccountScope, DisplayName = observation.DisplayName.Trim(),
                            RemarkName = observation.DisplayName.Trim(), BindingStatus = "name_match" };
                        repository.SaveContact(contact);
                    }
                    record.ContactId = contact.Id;
                    record.Text = answer.MessageText;
                    record.Status = "已记录"; record.VerifiedAt = DateTimeOffset.UtcNow;
                    record.Detail = "模型核对到新增的自己发出的祝福；不表示对方已收到或已读。";
                }
            }
            catch (OperationCanceledException) { record.Status = "待确认"; record.Detail = "核对已取消或超时，未重试。"; }
            catch (InvalidDataException e) { record.Status = "待确认"; record.Detail = e.Message; }
            catch (Exception) { record.Status = "待确认"; record.Detail = "未能明确核对发送内容或模型请求失败，请自行检查微信；未重试。"; }
            return repository.UpdateSentGreeting(record) ? record : null;
        }
        finally { _gate.Release(); }
    }
}

public sealed class SendGestureState
{
    private bool _leftPressed, _enterPressed;
    public bool Mouse(bool down, bool insideSend, bool targetActive, bool ownInput)
    {
        if (down) { _leftPressed = insideSend && targetActive && !ownInput; return false; }
        var send = _leftPressed && insideSend && targetActive && !ownInput;
        _leftPressed = false;
        return send;
    }
    public bool Enter(bool down, bool targetActive, bool editorFocused, bool ownInput, bool forbiddenModifiers)
    {
        if (!down) { _enterPressed = false; return false; }
        if (_enterPressed) return false;
        _enterPressed = true;
        return targetActive && editorFocused && !ownInput && !forbiddenModifiers;
    }
    public void Reset() { _leftPressed = false; _enterPressed = false; }
}
