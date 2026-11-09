using System;
using System.Drawing;

namespace WechatDraft.Windows;

internal sealed record EmptyEditorEvidence(bool BlankCanvas, Rectangle? Caret, bool ColoredCaret, int Background, string Reason,
    Rectangle? Placeholder = null);

internal static class VisionEditorEvidence
{
    internal static Rectangle Inner(Rectangle editor, uint dpi)
    {
        var x = Math.Max(2, (int)(4 * dpi / 96d)); var y = Math.Max(2, (int)(2 * dpi / 96d));
        var inner = Rectangle.FromLTRB(editor.Left + x, editor.Top + y, editor.Right - x, editor.Bottom - y);
        if (inner.Width < 20 || inner.Height < 16) throw new InvalidOperationException("可核对的编辑区域过小。");
        return inner;
    }
    internal static bool HasTextSelection(Bitmap before, Bitmap selected, Rectangle editor, uint dpi)
    {
        if (before.Size != selected.Size) return false;
        var area = Inner(editor, dpi);
        var old = VisionRegionSnapshot.Capture(before, area).Data;
        var current = VisionRegionSnapshot.Capture(selected, area).Data;
        var count = 0; var left = area.Width; var right = -1;
        for (var i = 0; i < current.Length; i += 4)
        {
            if (old.AsSpan(i, 4).SequenceEqual(current.AsSpan(i, 4))) continue;
            var b = current[i]; var g = current[i + 1]; var r = current[i + 2];
            var blue = b > r + 30 && b > g + 5 && g > r + 10;
            var green = g > r + 25 && g > b + 10 && b > r + 5;
            if (blue || green)
            { count++; var x = i / 4 % area.Width; left = Math.Min(left, x); right = Math.Max(right, x); }
        }
        return count >= Math.Max(8, 8 * dpi * dpi / (96 * 96)) && right - left + 1 >= Math.Max(4, 4 * dpi / 96);
    }
    internal static EmptyEditorEvidence InspectEmpty(Bitmap image, Rectangle editor, uint dpi)
    {
        var area = Inner(editor, dpi);
        var canvas = VisionRegionSnapshot.Capture(image, area);
        var pixels = canvas.Data;
        var hint = WechatEmptyPlaceholder.Find(canvas, dpi);
        var placeholder = hint is { } h ? new Rectangle(h.X + area.X, h.Y + area.Y, h.Width, h.Height) : (Rectangle?)null;
        var last = pixels.Length - 4;
        var background = Color.FromArgb(pixels[last + 2], pixels[last + 1], pixels[last]);
        var left = area.Width; var top = area.Height; var right = -1; var bottom = -1;
        var count = 0; var colored = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var x = i / 4 % area.Width; var y = i / 4 / area.Width;
            if (hint?.Contains(x, y) == true) continue;
            if (Math.Abs(pixels[i] - background.B) > 15 || Math.Abs(pixels[i + 1] - background.G) > 15 || Math.Abs(pixels[i + 2] - background.R) > 15)
            {
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                count++;
                var b = pixels[i]; var g = pixels[i + 1]; var r = pixels[i + 2];
                if ((g > r + 40 && g > b + 15) || (b > r + 40 && b > g + 15)) colored++;
            }
        }
        if (count == 0) return new(true, null, false, background.ToArgb(), "blank-without-visible-caret", placeholder);
        var scale = dpi / 96d;
        var width = right - left + 1; var height = bottom - top + 1;
        // Inspect the entire canvas, not just the click point. A single solid marker near
        // the start can be a caret; text, attachments and separated objects cannot.
        if (width > Math.Max(2, 3 * scale) || height < 10 * scale || height > 40 * scale ||
            left > 32 * scale || top > 24 * scale || count < width * height * .9)
            return new(false, null, false, background.ToArgb(), "nonblank-or-unrecognized-marker", placeholder);
        return new(true, new(area.Left + left, area.Top + top, width, height), colored >= count * .9,
            background.ToArgb(), colored >= count * .9 ? "colored-caret" : "caret-needs-blink", placeholder);
    }

    internal static bool SameEmptyCaret(EmptyEditorEvidence first, EmptyEditorEvidence second) =>
        first.BlankCanvas && second.BlankCanvas && first.Caret is not null && first.Caret == second.Caret && SameBackground(first, second);

    internal static EmptyEditorEvidence? ConfirmEmptyCaret(EmptyEditorEvidence current, EmptyEditorEvidence? previous = null)
    {
        if (!current.BlankCanvas) return null;
        if (current.Caret is not null && current.ColoredCaret) return current;
        if (previous is null || !previous.BlankCanvas || !SameBackground(previous, current)) return null;
        if (previous.Caret is not null && current.Caret is null) return previous;
        return previous.Caret is null && current.Caret is not null ? current : null;
    }

    internal static bool SameBackground(EmptyEditorEvidence first, EmptyEditorEvidence second)
    {
        var a = Color.FromArgb(first.Background); var b = Color.FromArgb(second.Background);
        return Math.Abs(a.R - b.R) <= 15 && Math.Abs(a.G - b.G) <= 15 && Math.Abs(a.B - b.B) <= 15;
    }
}
