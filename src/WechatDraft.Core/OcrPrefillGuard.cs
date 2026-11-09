namespace WechatDraft.Core;

// A title is scoped visible evidence, not a permanent contact identity.
public sealed class OcrPrefillGuard(string title, string originalText)
{
    public string Title { get; } = ValidateTitle(title);
    public string OriginalText { get; } = originalText;
    private string _reason = "";
    private int _pasteIssued;
    public string Reason => Volatile.Read(ref _reason);
    public bool PasteIssued => Volatile.Read(ref _pasteIssued) != 0;
    public bool Valid => Reason.Length == 0;

    public bool CheckTitle(string current) => Valid && (current.Trim() == Title || Reject("OCR 聊天标题已变化，未继续预填。"));
    public bool CheckDraft(string current) => Valid && (current == OriginalText || Reject("微信原草稿已变化，未继续预填。"));
    public bool Reject(string reason) { Interlocked.CompareExchange(ref _reason, reason, ""); return false; }
    public bool TryIssuePaste()
    {
        return Valid && Interlocked.CompareExchange(ref _pasteIssued, 1, 0) == 0 && Valid;
    }
    public static string ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 80 || title.Any(char.IsControl))
            throw new InvalidDataException("本地 OCR 未能读取有效聊天标题。");
        var value = title.Trim();
        var group = DraftRules.LooksLikeGroupTitle(value);
        DebugSession.Current?.Event("local-title-group-check", new { title = value, group, rule = "paired-member-count-with-ocr-spaces" });
        if (group) throw new GroupChatDetectedException();
        return value;
    }
}
