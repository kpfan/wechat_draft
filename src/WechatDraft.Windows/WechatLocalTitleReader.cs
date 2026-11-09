using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WechatDraft.Core;

namespace WechatDraft.Windows;

// Replace this implementation to use another local OCR engine; positioning stays unchanged.
public interface IChatTitleOcr
{
    string Name { get; }
    Task<string> ReadAsync(Bitmap titleImage, CancellationToken cancellationToken);
}

public sealed class WindowsChatTitleOcr : IChatTitleOcr
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OcrEngine? _engine;
    public string Name => "Windows.Media.Ocr";

    public async Task<string> ReadAsync(Bitmap titleImage, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _engine ??= OcrEngine.TryCreateFromLanguage(new Language("zh-Hans")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
            if (_engine is null) throw new InvalidDataException("本地 OCR 语言不可用，请安装 Windows OCR 语言或更换 OCR 实现。");
            var result = await RecognizeAsync(titleImage, cancellationToken);
            if (result.Lines.Count == 0)
            {
                using var enlarged = EnlargeText(titleImage);
                result = await RecognizeAsync(enlarged, cancellationToken);
            }
            var text = ReadSingleRow(result);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 80 || text.Any(char.IsControl))
                throw new InvalidDataException("本地 OCR 未能读取有效聊天标题，未继续预填。");
            DebugSession.Current?.WriteJson("ocr_title", new { engine = Name, title = text, lines = result.Lines.Count,
                elapsedMs = timer.Elapsed.TotalMilliseconds, identityProven = false });
            return text;
        }
        finally { _gate.Release(); }
    }

    private async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken ct)
    {
        using var bytes = new MemoryStream(); image.Save(bytes, ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        { writer.WriteBytes(bytes.ToArray()); await writer.StoreAsync().AsTask(ct); writer.DetachStream(); }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct);
        return await _engine!.RecognizeAsync(bitmap).AsTask(ct);
    }

    private static string ReadSingleRow(OcrResult result)
    {
        if (result.Lines.Count == 0) return "";
        var lines = result.Lines.Select(line => line.Words.Select(w => (w.Text,
            Bounds: new RectangleF((float)w.BoundingRect.X, (float)w.BoundingRect.Y, (float)w.BoundingRect.Width, (float)w.BoundingRect.Height))).ToArray()).ToArray();
        var spans = lines.Select(words => RectangleF.FromLTRB(words.Min(w => w.Bounds.Left), words.Min(w => w.Bounds.Top),
            words.Max(w => w.Bounds.Right), words.Max(w => w.Bounds.Bottom))).ToArray();
        var reference = spans.OrderByDescending(r => r.Height).First();
        foreach (var span in spans)
            if (Math.Min(reference.Bottom, span.Bottom) - Math.Max(reference.Top, span.Top) < Math.Min(reference.Height, span.Height) * .6)
                throw new InvalidDataException("标题出现不同文字行，无法确认当前聊天，未继续预填。");
        return WechatLocalTitleReader.JoinTitleWords(lines.SelectMany(words => words).OrderBy(w => w.Bounds.Left));
    }

    private static Bitmap EnlargeText(Bitmap image)
    {
        var background = image.GetPixel(0, 0); var left = image.Width; var top = image.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var color = image.GetPixel(x, y);
                if (Math.Abs(color.R - background.R) + Math.Abs(color.G - background.G) + Math.Abs(color.B - background.B) <= 90) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        if (right < 0) throw new InvalidDataException("标题区域没有可读取文字。");
        var bounds = Rectangle.FromLTRB(Math.Max(0, left - 8), Math.Max(0, top - 8), Math.Min(image.Width, right + 9), Math.Min(image.Height, bottom + 9));
        var scale = Math.Min(2d, OcrEngine.MaxImageDimension / (double)Math.Max(bounds.Width, bounds.Height));
        var enlarged = new Bitmap(Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale)), PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(enlarged); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(image, new Rectangle(0, 0, enlarged.Width, enlarged.Height), bounds, GraphicsUnit.Pixel);
        return enlarged;
    }
}

internal static class WechatLocalTitleReader
{
    internal static IChatTitleOcr Default { get; } = new WindowsChatTitleOcr();
    internal static async Task<string> ReadAsync(Bitmap image, VisionWindowFrame frame, VisionLayout layout, CancellationToken ct, IChatTitleOcr? ocr = null)
    {
        using var title = image.Clone(frame.ToClientRectangle(layout.Header!), PixelFormat.Format32bppArgb);
        return await (ocr ?? Default).ReadAsync(title, ct);
    }

    internal static string JoinTitleWords(IEnumerable<(string Text, RectangleF Bounds)> words)
    {
        var result = new StringBuilder(); RectangleF? previous = null;
        foreach (var (text, bounds) in words)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0 || !float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y))
                throw new InvalidDataException("本地 OCR 文字边界无效。");
            if (previous is { } before && bounds.Left - before.Right > Math.Max(before.Height, bounds.Height) * .18f)
                result.Append(' ');
            result.Append(text); previous = bounds;
        }
        return result.ToString().Trim();
    }
}
