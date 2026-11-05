using System;

namespace WechatDraft.Windows;

// Runtime identity within this UIA session, not a permanent WeChat friend ID.
internal sealed record UiaConversationIdentity(string Header, string Editor, string SelectedSession, string ChatView = "")
{
    public string BindingMethod => !string.IsNullOrWhiteSpace(ChatView) ? "chat-view" : "session-selection";

    public bool Matches(UiaConversationIdentity? current)
    {
        if (current is null || string.IsNullOrWhiteSpace(Header) || string.IsNullOrWhiteSpace(Editor) ||
            !string.Equals(Header, current.Header, StringComparison.Ordinal) ||
            !string.Equals(Editor, current.Editor, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrWhiteSpace(ChatView))
        {
            // A virtualized list item may disappear; the original open chat view must remain identical.
            // When both observations expose a selected item, contradictory selections still fail closed.
            return string.Equals(ChatView, current.ChatView, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(SelectedSession) || string.IsNullOrWhiteSpace(current.SelectedSession) ||
                 string.Equals(SelectedSession, current.SelectedSession, StringComparison.Ordinal));
        }
        // Older layouts without a verified chat view still require the original selected item.
        // Never downgrade a task from a chat-view binding to title-only or selection-only checks.
        return !string.IsNullOrWhiteSpace(SelectedSession) &&
            string.Equals(SelectedSession, current.SelectedSession, StringComparison.Ordinal);
    }
}
