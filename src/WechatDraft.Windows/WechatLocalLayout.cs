using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using WechatDraft.Core;
using Windows.UI.ViewManagement;

namespace WechatDraft.Windows;

// Geometry only. Toolbars and local edges do not prove chat identity, draft kind or single-chat state.
internal static class WechatLocalLayout
{
    private static readonly Dictionary<string, WechatToolbarTheme> SuccessfulThemes = new();

    internal static bool CanReuseGeometry(Bitmap initial, Bitmap current, uint dpi, VisionLayout layout,
        VisionAnchorHints anchors, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (initial.Size != current.Size ||
            !SamePixels(initial, current, Pixels(anchors.TopToolbar, initial.Size)) ||
            !SamePixels(initial, current, Pixels(anchors.BottomToolbar, initial.Size))) return false;
        try
        {
            var checkedLayout = FromAnchors(current, dpi, anchors, ct);
            return checkedLayout.Input!.Y == layout.Input!.Y;
        }
        catch (InvalidDataException) { return false; }
    }

    private static bool SamePixels(Bitmap initial, Bitmap current, Rectangle region) =>
        VisionRegionSnapshot.Capture(initial, region).Matches(VisionRegionSnapshot.Capture(current, region), allowCaret: false);

    private static Rectangle Pixels(VisionBox box, Size size) =>
        Rectangle.FromLTRB((int)Math.Round(box.X * size.Width / 1000), (int)Math.Round(box.Y * size.Height / 1000),
            (int)Math.Round(box.Right * size.Width / 1000), (int)Math.Round(box.Bottom * size.Height / 1000));

    internal static bool SameGeometry(VisionLayout baseline, VisionLayout current, int width, int height)
    {
        var a = new[] { baseline.Header, baseline.Input, baseline.Editor, baseline.Send, baseline.InputToolbar };
        var b = new[] { current.Header, current.Input, current.Editor, current.Send, current.InputToolbar };
        return a.Zip(b).All(pair => pair.First is { } first && pair.Second is { } second &&
            Math.Abs(first.X - second.X) * width / 1000 <= 2 && Math.Abs(first.Y - second.Y) * height / 1000 <= 2 &&
            Math.Abs(first.Right - second.Right) * width / 1000 <= 2 && Math.Abs(first.Bottom - second.Bottom) * height / 1000 <= 2);
    }

    internal static bool ConfirmMessageGeometry(Bitmap initial, Bitmap current, VisionWindowFrame frame,
        VisionLayout baseline, VisionAnchorHints anchors, CancellationToken ct)
    {
        if (CanReuseGeometry(initial, current, frame.Dpi, baseline, anchors, ct)) return true;
        if (initial.Size != current.Size) return false;
        var located = Locate(current, frame.Dpi, ct).Layout;
        if (!SameGeometry(baseline, located, current.Width, current.Height)) return false;
        var messages = frame.ToClientRectangle(baseline.Messages!);
        var currentMessages = frame.ToClientRectangle(located.Messages!);
        return messages.Left >= currentMessages.Left && messages.Right <= currentMessages.Right &&
            messages.Top >= frame.ToClientRectangle(located.Header!).Bottom &&
            messages.Bottom <= frame.ToClientRectangle(located.Input!).Top;
    }

    internal static (VisionLayout Layout, VisionAnchorHints Anchors) Locate(Bitmap image, uint dpi, CancellationToken ct,
        string? windowIdentity = null, Func<WechatToolbarTheme>? readWindowsTheme = null)
    {
        ct.ThrowIfCancellationRequested();
        WechatToolbarTheme? cached = null;
        if (windowIdentity is { Length: > 0 })
        {
            lock (SuccessfulThemes)
                if (SuccessfulThemes.TryGetValue(windowIdentity, out var theme)) cached = theme;
        }
        var preferred = cached ?? readWindowsTheme?.Invoke() ?? ReadWindowsTheme();
        VisionAnchorHints? matchedAnchors = null;
        VisionLayout? layout = null;
        InvalidDataException? layoutError = null;
        var result = WechatTemplateLocator.Locate(image, dpi, ct, preferred, anchors =>
        {
            matchedAnchors = anchors;
            try
            {
                layout = FromAnchors(image, dpi, anchors, ct);
                return true;
            }
            catch (InvalidDataException e)
            {
                layoutError ??= e;
                return false;
            }
        });
        DebugSession.Current?.WriteJson("template_match", new
        {
            method = "local-only", dpi, matched = matchedAnchors is not null, anchors = matchedAnchors,
            preferredTheme = preferred.ToString(), theme = result?.Theme.ToString(), cached = cached is not null
        });
        if (result is not { } confirmed || layout is null)
            throw layoutError ?? new InvalidDataException("本地工具栏模板未可靠匹配，请完整显示微信聊天，并检查主题或显示缩放。未调用大模型定位。");
        ct.ThrowIfCancellationRequested();
        if (windowIdentity is { Length: > 0 })
        {
            lock (SuccessfulThemes)
            {
                if (SuccessfulThemes.Count >= 32 && !SuccessfulThemes.ContainsKey(windowIdentity)) SuccessfulThemes.Clear();
                SuccessfulThemes[windowIdentity] = confirmed.Theme;
            }
        }
        return (layout, confirmed.Anchors);
    }

    private static WechatToolbarTheme ReadWindowsTheme()
    {
        try
        {
            var foreground = new UISettings().GetColorValue(UIColorType.Foreground);
            return 5 * foreground.G + 2 * foreground.R + foreground.B > 8 * 128
                ? WechatToolbarTheme.Dark : WechatToolbarTheme.Light;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            return WechatToolbarTheme.Dark;
        }
    }

    internal static VisionLayout FromAnchors(Bitmap image, uint dpi, VisionAnchorHints anchors, CancellationToken ct)
    {
        VisionRules.ValidateAnchors(anchors);
        if (dpi is < 96 or > 384 || Math.Abs(anchors.Scale - dpi / 192d) > .001)
            throw new System.IO.InvalidDataException("本地模板与窗口缩放不一致。");
        var scale = dpi / 96d;
        double Px(double value) => value * scale;
        var top = Pixels(anchors.TopToolbar, image.Size); var bottom = Pixels(anchors.BottomToolbar, image.Size);
        var left = bottom.Left - (int)Px(16);
        var right = image.Width - (int)Px(8);
        var titleTop = Math.Max(0, top.Top - (int)Px(8));
        var titleBottom = top.Bottom + (int)Px(8);
        var messageTop = top.Bottom + (int)Px(14);
        if (left < 0 || right - left < Px(250) || top.Left - left < Px(100) ||
            bottom.Right > right || messageTop >= bottom.Top)
            throw new System.IO.InvalidDataException("本地双锚点区域关系异常，未继续识别。");
        // Current WeChat puts the toolbar below the editor; older versions put it above.
        // A tall draft can put the separator above 40% of the window. Keep a usable
        // message area below the header and scan upwards from the input toolbar.
        var separator = FindSeparator(image, left, right, messageTop + (int)Px(64), messageTop + (int)Px(80),
            bottom.Top - (int)Px(3), Math.Max(2, (int)Px(3)), ct);
        if (separator < 0) throw new System.IO.InvalidDataException("本地未找到可靠的消息区与输入区分界，请调整微信窗口后重试。未调用大模型定位。");
        var inputBottom = image.Height - (int)Px(8);
        var toolbarAtTop = bottom.Top - separator < Px(40);
        var editorTop = toolbarAtTop ? bottom.Bottom + (int)Px(6) : separator + (int)Px(8);
        var editorBottom = toolbarAtTop ? inputBottom - (int)Px(45) : bottom.Top - (int)Px(8);
        var send = Rectangle.FromLTRB(right - (int)Px(66), inputBottom - (int)Px(34), right - (int)Px(8), inputBottom - (int)Px(6));
        VisionBox Box(double x, double y, double r, double b)
        {
            if (x < 0 || y < 0 || r > image.Width || b > image.Height || r - x < Px(8) || b - y < Px(8))
                throw new System.IO.InvalidDataException("本地推算区域过小或超出微信窗口，未继续识别。");
            return new() { X = x * 1000 / image.Width, Y = y * 1000 / image.Height,
                Width = (r - x) * 1000 / image.Width, Height = (b - y) * 1000 / image.Height };
        }
        return new VisionLayout
        {
            SchemaVersion = "1.0", Evidence = "local-toolbar-templates-and-input-edge",
            // Deliberately retain unknown semantic fields. This layout must never pass write validation.
            Chat = Box(left, titleTop, right, inputBottom),
            Header = Box(left + Px(4), titleTop, top.Left - Px(12), titleBottom),
            Messages = Box(left + Px(2), messageTop, right, separator - Px(4)),
            Input = Box(left, separator, right, inputBottom),
            Editor = Box(left + Px(8), editorTop, right - Px(8), editorBottom),
            Send = Box(send.Left, send.Top, send.Right, send.Bottom), InputToolbar = anchors.BottomToolbar
        };
    }

    private static int FindSeparator(Bitmap image, int left, int right, int start, int fallbackStart, int end, int gap,
        CancellationToken ct)
    {
        const int samples = 96;
        const int edgeSamples = 4;
        var inset = (right - left) / 16;
        var firstY = Math.Max(start, gap);
        var lastY = Math.Min(end, image.Height - gap - 1);
        if (lastY < firstY) return -1;
        ct.ThrowIfCancellationRequested();
        var spanWidth = right - left - inset * 2;
        var region = new Rectangle(left, firstY - gap, right - left + 1, lastY - firstY + gap * 2 + 1);
        var pixels = VisionRegionSnapshot.Capture(image, region).Data;
        var stride = region.Width * 4;
        var gapBytes = gap * stride;
        Span<int> sampleOffsets = stackalloc int[samples];
        for (var i = 0; i < samples; i++) sampleOffsets[i] = (inset + spanWidth * i / (samples - 1)) * 4;
        var partlyObscured = -1;
        var ambiguous = false;
        var outlined = -1;
        var outlineAmbiguous = false;
        for (var y = lastY; y >= firstY; y--)
        {
            ct.ThrowIfCancellationRequested();
            var dark = 0; var light = 0;
            var darkBelow = 0; var lightBelow = 0;
            var row = (y - region.Top) * stride;
            foreach (var offset in sampleOffsets)
            {
                var index = row + offset;
                var center = Gray(pixels, index);
                var before = Gray(pixels, index - gapBytes); var after = Gray(pixels, index + gapBytes);
                if (center - after >= 3)
                {
                    lightBelow++;
                    if (center - before >= 3) light++;
                }
                else if (after - center >= 3)
                {
                    darkBelow++;
                    if (before - center >= 3) dark++;
                }
            }
            if (Math.Max(dark, light) >= samples * .85) return y;
            // A shorter message pane still needs a full, two-sided divider.
            if (y < fallbackStart) continue;
            if (Math.Max(darkBelow, lightBelow) >= 90 && y + 7 * gap <= lastY &&
                HasEditorOutline(pixels, region.Width, row, gap, sampleOffsets, lightBelow >= darkBelow ? 1 : -1))
            {
                if (outlined < 0) outlined = y;
                else if (outlined - y > gap) outlineAmbiguous = true;
            }

            // A message touching the divider can change the reference pixels above it.
            var direction = light >= dark ? 1 : -1;
            if (Math.Max(dark, light) < 72 || (direction > 0 ? lightBelow : darkBelow) < 90) continue;
            var leftEdge = 0;
            var rightEdge = 0;
            for (var i = 0; i < edgeSamples; i++)
            {
                if (HasTwoSidedContrast(pixels, row + sampleOffsets[i], gapBytes, direction)) leftEdge++;
                if (HasTwoSidedContrast(pixels, row + sampleOffsets[samples - 1 - i], gapBytes, direction)) rightEdge++;
            }
            if (leftEdge < 3 || rightEdge < 3) continue;
            if (partlyObscured < 0) partlyObscured = y;
            else if (partlyObscured - y > gap) ambiguous = true;
        }
        if (partlyObscured >= 0 && !ambiguous) return partlyObscured;
        return outlineAmbiguous ? -1 : outlined;
    }

    private static bool HasEditorOutline(byte[] pixels, int width, int row, int gap, ReadOnlySpan<int> sampleOffsets, int direction)
    {
        var stride = width * 4;
        var tone = Gray(pixels, row + sampleOffsets[sampleOffsets.Length / 2]);
        var matchingTop = 0;
        foreach (var offset in sampleOffsets)
        {
            var center = Gray(pixels, row + offset);
            if (Math.Abs(center - tone) <= 3 && direction * (center - Gray(pixels, row + offset + gap * stride)) >= 3)
                matchingTop++;
        }
        if (matchingTop < 90) return false;

        // Antialiasing can change the apparent tone between the horizontal and vertical strokes.
        var probe = Math.Max(2, gap / 2);
        int StrokeContrast(int x, int relativeY)
        {
            var index = row + relativeY * stride + x * 4;
            var center = Gray(pixels, index);
            if (Math.Abs(center - tone) > 6) return 0;
            return Math.Min(direction * (center - Gray(pixels, index - probe * 4)),
                direction * (center - Gray(pixels, index + probe * 4)));
        }
        bool Corner(int edge, int inward)
        {
            // Confirm a straight side below the top, and the rounded turn inward near it.
            // A horizontal line within an existing editor cannot start both of its sides.
            for (var offset = probe; offset <= 6 * gap; offset++)
            {
                var x = edge + inward * offset;
                if (StrokeContrast(x, 3 * gap) < 3 || StrokeContrast(x, 5 * gap) < 3 ||
                    StrokeContrast(x, 7 * gap) < 3 || StrokeContrast(x, -gap) >= 3) continue;
                // At the turn, an inward stroke must be stronger than any fringe on the straight side.
                var turnContrast = Math.Max(0, StrokeContrast(x, gap)) + 3;
                for (var shift = 1; shift <= 2 * gap; shift++)
                    if (StrokeContrast(x + inward * shift, gap) >= turnContrast) return true;
            }
            return false;
        }
        return Corner(0, 1) && Corner(width - 1, -1);
    }

    private static bool HasTwoSidedContrast(byte[] pixels, int index, int distance, int direction)
    {
        var center = Gray(pixels, index);
        return direction * (center - Gray(pixels, index - distance)) >= 3 &&
            direction * (center - Gray(pixels, index + distance)) >= 3;
    }

    private static int Gray(byte[] pixels, int index) =>
        (pixels[index + 2] * 77 + pixels[index + 1] * 150 + pixels[index] * 29) >> 8;
}
