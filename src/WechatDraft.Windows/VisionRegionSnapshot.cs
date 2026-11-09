using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WechatDraft.Core;

namespace WechatDraft.Windows;

internal sealed record VisionRegionSnapshot(int Width, int Height, byte[] Data)
{
    public static VisionRegionSnapshot CaptureInput(Bitmap bitmap, VisionLayout layout, uint dpi = 96, bool normalizeEmptyHint = false,
        Rectangle? knownEmptyHint = null, Action<Rectangle>? hintObserved = null)
    {
        var box = layout.Input ?? throw new InvalidOperationException("输入面板尚未定位。");
        var region = Rectangle.FromLTRB((int)Math.Ceiling(box.X * bitmap.Width / 1000),
            (int)Math.Ceiling(box.Y * bitmap.Height / 1000), (int)Math.Floor(box.Right * bitmap.Width / 1000),
            (int)Math.Floor(box.Bottom * bitmap.Height / 1000));
        var captured = Capture(bitmap, region);
        if (!normalizeEmptyHint || layout.Editor is not { } editor) return captured;
        var editorRegion = Rectangle.FromLTRB((int)Math.Ceiling(editor.X * bitmap.Width / 1000),
            (int)Math.Ceiling(editor.Y * bitmap.Height / 1000), (int)Math.Floor(editor.Right * bitmap.Width / 1000),
            (int)Math.Floor(editor.Bottom * bitmap.Height / 1000));
        var inner = VisionEditorEvidence.Inner(editorRegion, dpi);
        var canvas = Capture(bitmap, inner);
        var recognized = WechatEmptyPlaceholder.Find(canvas, dpi);
        Rectangle hint;
        if (recognized is { } found) { hint = found; hint.Offset(inner.X, inner.Y); }
        else if (knownEmptyHint is { } known && inner.Contains(known)) hint = known;
        else return captured;
        if (!region.Contains(hint)) return captured;
        if (recognized is not null) hintObserved?.Invoke(hint);
        hint.Offset(-region.X, -region.Y);
        var background = canvas.Data.Length - 4;
        var fringe = hint; fringe.Inflate(Math.Max(1, (int)Math.Ceiling(dpi / 96d)), Math.Max(1, (int)Math.Ceiling(dpi / 96d)));
        fringe.Intersect(new(0, 0, captured.Width, captured.Height));
        for (var y = fringe.Top; y < fringe.Bottom; y++)
            for (var x = fringe.Left; x < fringe.Right; x++)
            {
                var i = (y * captured.Width + x) * 4;
                // Glyph antialiasing can extend just outside the matched ink bounds.
                // Only near-background fringe pixels are decoration; stronger changes remain visible.
                if ((recognized is not null && hint.Contains(x, y)) ||
                    (Math.Abs(captured.Data[i] - canvas.Data[background]) <= 15 &&
                     Math.Abs(captured.Data[i + 1] - canvas.Data[background + 1]) <= 15 &&
                     Math.Abs(captured.Data[i + 2] - canvas.Data[background + 2]) <= 15))
                    canvas.Data.AsSpan(background, 4).CopyTo(captured.Data.AsSpan(i, 4));
            }
        return captured;
    }

    public byte[] ToPng()
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < Height; y++) Marshal.Copy(Data, y * Width * 4, nint.Add(locked.Scan0, y * locked.Stride), Width * 4);
        }
        finally { bitmap.UnlockBits(locked); }
        using var output = new MemoryStream(); bitmap.Save(output, ImageFormat.Png); return output.ToArray();
    }

    public (int Pixels, Rectangle? Bounds) Difference(VisionRegionSnapshot other)
    {
        if (Width != other.Width || Height != other.Height) return (Math.Max(Width * Height, other.Width * other.Height), null);
        var count = 0; var left = Width; var top = Height; var right = -1; var bottom = -1;
        for (var i = 0; i < Data.Length; i += 4)
            if (!Data.AsSpan(i, 4).SequenceEqual(other.Data.AsSpan(i, 4)))
            {
                count++; var x = i / 4 % Width; var y = i / 4 / Width;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
        return (count, count == 0 ? null : Rectangle.FromLTRB(left, top, right + 1, bottom + 1));
    }

    public static VisionRegionSnapshot Capture(Bitmap bitmap, Rectangle region)
    {
        using var copy = bitmap.Clone(region, PixelFormat.Format32bppArgb);
        var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * data.Height]; Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new(copy.Width, copy.Height, bytes);
        }
        finally { copy.UnlockBits(data); }
    }

    public bool Matches(VisionRegionSnapshot after, bool allowCaret, uint dpi = 96)
    {
        if (Width != after.Width || Height != after.Height) return false;
        if (Data.AsSpan().SequenceEqual(after.Data)) return true;
        if (!allowCaret || dpi is < 96 or > 384) return false;
        var minX = Width; var maxX = -1; var minY = Height; var maxY = -1; var changed = 0;
        for (var i = 0; i < Data.Length; i += 4)
            if (!Data.AsSpan(i, 4).SequenceEqual(after.Data.AsSpan(i, 4)))
            {
                var x = i / 4 % Width; var y = i / 4 / Width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); changed++;
            }
        var scale = dpi / 96d;
        return changed <= 144 * scale * scale && maxX - minX + 1 <= 3 * scale &&
            maxY - minY + 1 >= 6 * scale && maxY - minY + 1 <= 48 * scale;
    }
}
