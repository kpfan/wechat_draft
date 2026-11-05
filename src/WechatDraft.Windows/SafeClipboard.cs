using System;

namespace WechatDraft.Windows;

internal sealed class SafeClipboard : IDisposable
{
    private readonly ClipboardSession _session;
    public SafeClipboard(nint owner, string text)
    {
        _session = new(owner);
        try { _session.PutText(text); }
        catch { _session.Dispose(); throw; }
    }
    public void Dispose() => _session.Dispose();
}
