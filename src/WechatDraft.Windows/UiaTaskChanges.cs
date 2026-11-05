using System;
using System.Threading;

namespace WechatDraft.Windows;

// Sticky changes to the bound chat or draft, rather than raw mouse/keyboard activity.
internal sealed class UiaTaskChanges(string originalText, string originalControlValue, string header, UiaConversationIdentity? identity)
{
    private string? _reason;
    public bool Changed => Volatile.Read(ref _reason) is not null;
    public string? Reason => Volatile.Read(ref _reason);

    public void ObserveDraft(string current)
    { if (!string.Equals(originalText, current, StringComparison.Ordinal)) Unavailable("draft-changed"); }

    public void ObserveControlValue(string current)
    { if (!string.Equals(originalControlValue, current, StringComparison.Ordinal)) Unavailable("draft-changed"); }

    public void ObserveHeader(string current)
    { if (!string.Equals(header, current, StringComparison.Ordinal)) Unavailable("chat-changed"); }

    public void ObserveConversation(UiaConversationIdentity? current)
    { if (identity is null || !identity.Matches(current)) Unavailable("chat-changed"); }

    public void Unavailable(string reason = "uia-state-unavailable") => Interlocked.CompareExchange(ref _reason, reason, null);
}
