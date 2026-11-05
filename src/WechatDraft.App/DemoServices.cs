using WechatDraft.Core;

namespace WechatDraft.App;

internal sealed class DemoConversationAdapter(Func<string> readText, Action<string> writeText, Func<string> readName, Func<long> revision) : IConversationAdapter
{
    private string _name = "";
    public Task<ConversationSnapshot> BeginAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); _name = readName();
        return Task.FromResult(new ConversationSnapshot(Guid.NewGuid().ToString("N"), _name, readText(), "demo", true, revision()));
    }
    public Task<Capture> CaptureAsync(ConversationSnapshot snapshot, int index, CancellationToken cancellationToken) =>
        Task.FromResult(new Capture($"capture_{index}", [1, 2, 3, (byte)index]));
    public Task<bool> ScrollUpAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> IsUnchangedAsync(ConversationSnapshot snapshot, CancellationToken cancellationToken) =>
        Task.FromResult(snapshot.DisplayName == readName() && snapshot.OriginalText == readText() && Equals(snapshot.NativeState, revision()));
    public Task<FillResult> AppendAsync(ConversationSnapshot snapshot, string greeting, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.DisplayName != readName() || snapshot.OriginalText != readText() || !Equals(snapshot.NativeState, revision())) return Task.FromResult(new FillResult("待手动处理", "演示草稿发生变化，未追加。"));
        writeText(DraftRules.Append(snapshot.OriginalText, greeting));
        return Task.FromResult(new FillResult("演示预填", "已填入右侧演示输入框，不会发送消息。", true));
    }
    public Task RestoreAsync(ConversationSnapshot snapshot) => Task.CompletedTask;
}

internal sealed class DemoGreetingModel : IGreetingModel
{
    public async Task<ModelResponse> GenerateAsync(AppSettings settings, string apiKey, IReadOnlyList<Capture> captures,
        Contact? contact, IReadOnlyList<BlessingExample> examples, bool finalRound, CancellationToken cancellationToken)
    {
        await Task.Delay(1100, cancellationToken);
        var text = examples.FirstOrDefault()?.Text ?? $"{settings.Scene}快乐！愿你在接下来的日子里平安顺心，忙碌之余也有轻松与欢笑，每一天都有值得期待的小事。";
        var address = contact?.AddressTerm;
        text = string.IsNullOrWhiteSpace(address) ? text.Replace("{称呼}，", "").Replace("{称呼}", "") : text.Replace("{称呼}", address);
        if (!string.IsNullOrWhiteSpace(address) && !text.StartsWith(address.Trim(), StringComparison.Ordinal)) text = address.Trim() + "，" + text;
        return new() { ContextSufficient = true, RelationshipType = contact?.Relationship ?? "unknown",
            AddressTerm = address, GreetingText = text };
    }
}
