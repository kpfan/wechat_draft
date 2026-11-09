using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WechatDraft.Core;

namespace WechatDraft.Windows;

// Task-local only: an unchanged title crop has exactly the same OCR input.
// Window identity, geometry, foreground and draft checks remain the caller's responsibility.
internal sealed class ExactTitleOcrCache(IChatTitleOcr inner) : IChatTitleOcr
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VisionRegionSnapshot? _image;
    private string? _text;
    public string Name => inner.Name;

    public async Task<string> ReadAsync(Bitmap titleImage, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = VisionRegionSnapshot.Capture(titleImage, new(0, 0, titleImage.Width, titleImage.Height));
            if (_text is not null && _image?.Matches(current, allowCaret: false) == true)
            {
                DebugSession.Current?.Event("ocr-title-cache-hit", new { engine = Name });
                return _text;
            }
            var text = await inner.ReadAsync(titleImage, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _image = current;
            _text = text;
            return text;
        }
        finally { _gate.Release(); }
    }
}
