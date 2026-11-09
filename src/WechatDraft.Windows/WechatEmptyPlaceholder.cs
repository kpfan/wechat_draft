using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace WechatDraft.Windows;

// The built-in image contains only WeChat's public "长按鼠标 语音输入" hint.
// Match its glyphs and muted appearance; arbitrary gray text is never ignored.
internal static class WechatEmptyPlaceholder
{
    internal static Bitmap Load(uint dpi)
    {
        using var stream = typeof(WechatEmptyPlaceholder).Assembly.GetManifestResourceStream(
            "WechatDraft.Windows.Templates.wechat_empty_placeholder.png") ?? throw new InvalidOperationException("空输入提示模板缺失。");
        using var source = new Bitmap(stream);
        return Resize(source, (int)Math.Round(source.Width * dpi / 192d), (int)Math.Round(source.Height * dpi / 192d));
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image);
        g.InterpolationMode = source.Size == image.Size ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(source, new Rectangle(0, 0, width, height), new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
        return image;
    }

    internal static Rectangle? Find(VisionRegionSnapshot canvas, uint dpi)
    {
        if (dpi is < 96 or > 384) return null;
        var scale = dpi / 96d;
        var pixels = canvas.Data; var bg = pixels.Length - 4;
        var left = canvas.Width; var top = canvas.Height; var right = -1; var bottom = -1;
        var searchRight = Math.Min(canvas.Width, (int)(180 * scale));
        var searchBottom = Math.Min(canvas.Height, (int)(50 * scale));
        int Contrast(int i) => Math.Max(Math.Abs(pixels[i] - pixels[bg]), Math.Max(
            Math.Abs(pixels[i + 1] - pixels[bg + 1]), Math.Abs(pixels[i + 2] - pixels[bg + 2])));
        bool Neutral(int i)
        {
            var high = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            var low = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            return high - low <= 12 && Contrast(i) <= 80;
        }
        var peak = 0;
        for (var y = 0; y < searchBottom; y++)
            for (var x = 0; x < searchRight; x++)
            {
                var i = (y * canvas.Width + x) * 4;
                if (Neutral(i)) peak = Math.Max(peak, Contrast(i));
            }
        if (peak <= 1) return null;
        // Find glyph bounds relative to their current opacity, including the hover fade.
        for (var y = 0; y < searchBottom; y++)
            for (var x = 0; x < searchRight; x++)
            {
                var i = (y * canvas.Width + x) * 4;
                if (Neutral(i) && Contrast(i) > peak / 3d)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
            }
        if (right < 0 || left > 32 * scale || top > 24 * scale) return null;
        var area = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        using var expected = Load(dpi);
        if (Math.Abs(area.Width - expected.Width) > Math.Max(2, 2 * scale) ||
            Math.Abs(area.Height - expected.Height) > Math.Max(2, 2 * scale)) return null;
        var reference = VisionRegionSnapshot.Capture(expected, new(0, 0, expected.Width, expected.Height)).Data;
        var values = new double[expected.Width * expected.Height]; double sum = 0;
        for (var i = 0; i < values.Length; i++)
            sum += values[i] = Math.Max(Math.Abs(reference[i * 4] - 31), Math.Max(
                Math.Abs(reference[i * 4 + 1] - 31), Math.Abs(reference[i * 4 + 2] - 31)));
        var mean = sum / values.Length; double energy = 0;
        for (var i = 0; i < values.Length; i++) { values[i] -= mean; energy += values[i] * values[i]; }
        double Score(int px, int py)
        {
            double total = 0, squares = 0, product = 0;
            for (var y = 0; y < expected.Height; y++)
                for (var x = 0; x < expected.Width; x++)
                {
                    var i = ((py + y) * canvas.Width + px + x) * 4;
                    if (Contrast(i) > 2 && !Neutral(i)) return -1;
                    var value = Contrast(i); total += value; squares += value * value;
                    product += value * values[y * expected.Width + x];
                }
            var actualEnergy = squares - total * total / values.Length;
            return actualEnergy <= values.Length * .01 || energy <= 0 ? -1 : product / Math.Sqrt(actualEnergy * energy);
        }
        var best = .98; Rectangle? match = null;
        for (var y = Math.Max(0, area.Top - 2); y <= Math.Min(canvas.Height - expected.Height, area.Top + 2); y++)
            for (var x = Math.Max(0, area.Left - 2); x <= Math.Min(canvas.Width - expected.Width, area.Left + 2); x++)
            {
                var score = Score(x, y);
                if (score >= best) { best = score; match = new(x, y, expected.Width, expected.Height); }
            }
        return match;
    }
}
