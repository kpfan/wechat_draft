using System;

namespace WechatDraft.Windows;

internal sealed class VisionClipboard(nint owner) : IDisposable
{
    private readonly ClipboardSession _session = new(owner);
    public void PutText(string value) => _session.PutText(value);
    public bool TryReadCopied(uint sourceProcess, out string? text) => _session.TryReadCopied(sourceProcess, out text);
    public void Dispose() => _session.Dispose();
}
