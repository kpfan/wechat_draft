using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using WechatDraft.Core;

namespace WechatDraft.Windows;

// v0 reference: match only the selected window crop. Results stay window-relative until an action.
internal static class WechatTemplateLocator
{
    private const double MinimumScore = 0.80;
    private const double UniquenessGap = 0.035;
    private const int MaximumCandidates = 16;
    private sealed record GrayImage(int Width, int Height, byte[] Pixels);
    private sealed record Peak(int X, int Y, double Score);
    private sealed record Match(Rectangle Bounds, double Score);
    private sealed record Sample(int X, int Y, double Centered);
    private sealed record TemplateSamples(int Width, int Height, Sample[] Samples, double Energy);

    internal static VisionAnchorHints? Locate(Bitmap screenshot, uint dpi, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (dpi is < 96 or > 384 || screenshot.Width < 400 || screenshot.Height < 350 ||
            (long)screenshot.Width * screenshot.Height > 20000000) return null;
        var budget = new SearchBudget(ct);
        try
        {
            var scale = dpi / 192d;
            using var top = LoadTemplate("wechat_top_template.png", scale);
            using var bottom = LoadTemplate("wechat_bottom_template.png", scale);
            if (top is null || bottom is null) return null;
            if (top.Width >= screenshot.Width || bottom.Width >= screenshot.Width ||
                top.Height >= screenshot.Height || bottom.Height >= screenshot.Height) return null;
            var frame = ToGray(screenshot, budget);
            var coarseFrame = Half(frame, budget);
            var topMatch = Find(frame, coarseFrame, ToGray(top, budget),
                Rectangle.FromLTRB(screenshot.Width * 35 / 100, 0, screenshot.Width, screenshot.Height * 32 / 100), budget);
            if (topMatch is null) return null;
            var bottomMatch = Find(frame, coarseFrame, ToGray(bottom, budget),
                Rectangle.FromLTRB(screenshot.Width * 15 / 100, screenshot.Height * 35 / 100,
                    screenshot.Width, screenshot.Height), budget);
            if (bottomMatch is null || topMatch.Bounds.Bottom + topMatch.Bounds.Height >= bottomMatch.Bounds.Top ||
                topMatch.Bounds.Right <= bottomMatch.Bounds.Left ||
                topMatch.Bounds.Left + topMatch.Bounds.Width / 2 <= bottomMatch.Bounds.Left) return null;
            budget.Check();
            VisionBox Normalize(Rectangle rectangle) => new()
            {
                X = rectangle.X * 1000d / frame.Width, Y = rectangle.Y * 1000d / frame.Height,
                Width = rectangle.Width * 1000d / frame.Width, Height = rectangle.Height * 1000d / frame.Height
            };
            return new() { TopToolbar = Normalize(topMatch.Bounds), BottomToolbar = Normalize(bottomMatch.Bounds),
                TopScore = topMatch.Score, BottomScore = bottomMatch.Score, Scale = scale };
        }
        catch (Exception e) when (e is TimeoutException or ArgumentException or ExternalException or IOException)
        { return null; }
    }

    private static Bitmap? LoadTemplate(string name, double scale)
    {
        using var stream = typeof(WechatTemplateLocator).Assembly.GetManifestResourceStream("WechatDraft.Windows.Templates." + name);
        if (stream is null) return null;
        using var image = new Bitmap(stream);
        var width = (int)Math.Round(image.Width * scale); var height = (int)Math.Round(image.Height * scale);
        if (width < 10 || height < 10) return null;
        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            resized.SetResolution(96, 96);
            using var graphics = Graphics.FromImage(resized);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = scale == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(image, new Rectangle(0, 0, width, height), new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
            return resized;
        }
        catch { resized.Dispose(); throw; }
    }

    private static GrayImage ToGray(Bitmap bitmap, SearchBudget budget)
    {
        using var copy = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), PixelFormat.Format32bppArgb);
        var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var result = new byte[copy.Width * copy.Height]; var row = new byte[copy.Width * 4];
            for (var y = 0; y < copy.Height; y++)
            {
                budget.Check(); Marshal.Copy(nint.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (var x = 0; x < copy.Width; x++)
                    result[y * copy.Width + x] = (byte)((29 * row[x * 4] + 150 * row[x * 4 + 1] + 77 * row[x * 4 + 2] + 128) >> 8);
            }
            return new(copy.Width, copy.Height, result);
        }
        finally { copy.UnlockBits(data); }
    }

    private static GrayImage Half(GrayImage image, SearchBudget budget)
    {
        var width = image.Width / 2; var height = image.Height / 2; var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            budget.Check();
            for (var x = 0; x < width; x++)
            {
                var index = y * 2 * image.Width + x * 2;
                pixels[y * width + x] = (byte)((image.Pixels[index] + image.Pixels[index + 1] +
                    image.Pixels[index + image.Width] + image.Pixels[index + image.Width + 1] + 2) / 4);
            }
        }
        return new(width, height, pixels);
    }

    private static TemplateSamples Samples(GrayImage image, int limit)
    {
        var step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(image.Width * image.Height / (double)limit)));
        var raw = new List<(int X, int Y, double Value)>();
        for (var y = 0; y < image.Height; y += step)
            for (var x = 0; x < image.Width; x += step) raw.Add((x, y, image.Pixels[y * image.Width + x]));
        var mean = raw.Average(sample => sample.Value);
        var samples = raw.Select(sample => new Sample(sample.X, sample.Y, sample.Value - mean)).ToArray();
        return new(image.Width, image.Height, samples, samples.Sum(sample => sample.Centered * sample.Centered));
    }

    private static double Score(GrayImage frame, TemplateSamples template, int x, int y)
    {
        if (template.Energy < template.Samples.Length * 4) return -1;
        double sum = 0, squares = 0, product = 0;
        foreach (var sample in template.Samples)
        {
            var value = frame.Pixels[(y + sample.Y) * frame.Width + x + sample.X];
            sum += value; squares += value * value; product += value * sample.Centered;
        }
        var energy = squares - sum * sum / template.Samples.Length;
        return energy < template.Samples.Length * 4 ? -1 : Math.Clamp(product / Math.Sqrt(energy * template.Energy), -1, 1);
    }

    private static Match? Find(GrayImage frame, GrayImage coarseFrame, GrayImage template, Rectangle roi, SearchBudget budget)
    {
        var coarseTemplate = Half(template, budget);
        var coarseSamples = Samples(coarseTemplate, 128);
        var samples = Samples(template, 640);
        var peaks = new List<Peak>();
        var minX = (roi.Left + 1) / 2; var minY = (roi.Top + 1) / 2;
        var maxX = Math.Min(coarseFrame.Width - coarseTemplate.Width, (roi.Right - template.Width) / 2);
        var maxY = Math.Min(coarseFrame.Height - coarseTemplate.Height, (roi.Bottom - template.Height) / 2);
        for (var y = minY; y <= maxY; y++)
        {
            budget.Check();
            for (var x = minX; x <= maxX; x++)
            {
                var score = Score(coarseFrame, coarseSamples, x, y);
                if (score >= 0.25) KeepPeak(peaks, new(x, y, score), coarseTemplate.Width, coarseTemplate.Height);
            }
        }
        var refined = new List<Peak>();
        foreach (var peak in peaks)
        {
            for (var y = Math.Max(roi.Top, peak.Y * 2 - 3); y <= Math.Min(roi.Bottom - template.Height, peak.Y * 2 + 3); y++)
            {
                budget.Check();
                for (var x = Math.Max(roi.Left, peak.X * 2 - 3); x <= Math.Min(roi.Right - template.Width, peak.X * 2 + 3); x++)
                    KeepPeak(refined, new(x, y, Score(frame, samples, x, y)), template.Width, template.Height);
            }
        }
        var ordered = refined.OrderByDescending(peak => peak.Score).ToArray();
        if (ordered.Length == 0 || ordered[0].Score < MinimumScore ||
            (ordered.Length > 1 && ordered[0].Score - ordered[1].Score < UniquenessGap)) return null;
        return new(new Rectangle(ordered[0].X, ordered[0].Y, template.Width, template.Height), ordered[0].Score);
    }

    private static void KeepPeak(List<Peak> peaks, Peak candidate, int width, int height)
    {
        for (var i = 0; i < peaks.Count; i++)
            if (Math.Abs(peaks[i].X - candidate.X) < Math.Max(4, width / 2) &&
                Math.Abs(peaks[i].Y - candidate.Y) < Math.Max(4, height / 2))
            {
                if (candidate.Score > peaks[i].Score) peaks[i] = candidate;
                return;
            }
        if (peaks.Count < MaximumCandidates) { peaks.Add(candidate); return; }
        var minimum = 0;
        for (var i = 1; i < peaks.Count; i++) if (peaks[i].Score < peaks[minimum].Score) minimum = i;
        if (candidate.Score > peaks[minimum].Score) peaks[minimum] = candidate;
    }

    private sealed class SearchBudget(CancellationToken ct)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public void Check()
        {
            ct.ThrowIfCancellationRequested();
            if (_watch.ElapsedMilliseconds > 2500) throw new TimeoutException("Local template search exceeded its time budget.");
        }
    }
}
