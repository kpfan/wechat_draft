using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using WechatDraft.Core;

namespace WechatDraft.Windows;

// Geometry only. Toolbars and local edges do not prove chat identity, draft kind or single-chat state.
internal static class WechatLocalLayout
{
    internal static bool SameGeometry(VisionLayout baseline, VisionLayout current, int width, int height)
    {
        var a = new[] { baseline.Header, baseline.Input, baseline.Editor, baseline.Send, baseline.InputToolbar };
        var b = new[] { current.Header, current.Input, current.Editor, current.Send, current.InputToolbar };
        return a.Zip(b).All(pair => pair.First is { } first && pair.Second is { } second &&
            Math.Abs(first.X - second.X) * width / 1000 <= 2 && Math.Abs(first.Y - second.Y) * height / 1000 <= 2 &&
            Math.Abs(first.Right - second.Right) * width / 1000 <= 2 && Math.Abs(first.Bottom - second.Bottom) * height / 1000 <= 2);
    }

    internal static (VisionLayout Layout, VisionAnchorHints Anchors) Locate(Bitmap image, uint dpi, CancellationToken ct)
    {
        var anchors = WechatTemplateLocator.Locate(image, dpi, ct);
        DebugSession.Current?.WriteJson("template_match", new { method = "local-only", dpi, matched = anchors is not null, anchors });
        if (anchors is null)
            throw new System.IO.InvalidDataException("本地工具栏模板未可靠匹配，请完整显示微信聊天，并检查主题或显示缩放。未调用大模型定位。");
        return (FromAnchors(image, dpi, anchors, ct), anchors);
    }

    internal static VisionLayout FromAnchors(Bitmap image, uint dpi, VisionAnchorHints anchors, CancellationToken ct)
    {
        VisionRules.ValidateAnchors(anchors);
        if (dpi is < 96 or > 384 || Math.Abs(anchors.Scale - dpi / 192d) > .001)
            throw new System.IO.InvalidDataException("本地模板与窗口缩放不一致。");
        var scale = dpi / 96d;
        double Px(double value) => value * scale;
        Rectangle Pixels(VisionBox box) => Rectangle.FromLTRB((int)Math.Round(box.X * image.Width / 1000),
            (int)Math.Round(box.Y * image.Height / 1000), (int)Math.Round(box.Right * image.Width / 1000),
            (int)Math.Round(box.Bottom * image.Height / 1000));
        var top = Pixels(anchors.TopToolbar); var bottom = Pixels(anchors.BottomToolbar);
        var left = bottom.Left - (int)Px(16);
        var right = image.Width - (int)Px(8);
        var titleTop = Math.Max(0, top.Top - (int)Px(8));
        var titleBottom = top.Bottom + (int)Px(8);
        var messageTop = top.Bottom + (int)Px(14);
        if (left < image.Width * .15 || right - left < Px(250) || top.Left - left < Px(100) ||
            bottom.Right > right || messageTop >= bottom.Top)
            throw new System.IO.InvalidDataException("本地双锚点区域关系异常，未继续识别。");
        // Current WeChat puts the toolbar below the editor; older versions put it above.
        // Find the actual input separator instead of adding a fixed offset below the toolbar.
        var separator = FindSeparator(image, left, right, Math.Max(messageTop + (int)Px(80), image.Height * 2 / 5),
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

    private static int FindSeparator(Bitmap image, int left, int right, int start, int end, int gap, CancellationToken ct)
    {
        const int samples = 96;
        var inset = (right - left) / 16;
        for (var y = Math.Min(end, image.Height - gap - 1); y >= Math.Max(start, gap); y--)
        {
            ct.ThrowIfCancellationRequested();
            var dark = 0; var light = 0;
            for (var i = 0; i < samples; i++)
            {
                var x = left + inset + (right - left - inset * 2) * i / (samples - 1);
                var center = Gray(image.GetPixel(x, y));
                var before = Gray(image.GetPixel(x, y - gap)); var after = Gray(image.GetPixel(x, y + gap));
                if (center - before >= 3 && center - after >= 3) light++;
                if (before - center >= 3 && after - center >= 3) dark++;
            }
            if (Math.Max(dark, light) >= samples * .85) return y;
        }
        return -1;
    }

    private static int Gray(Color color) => (color.R * 77 + color.G * 150 + color.B * 29) >> 8;
}
