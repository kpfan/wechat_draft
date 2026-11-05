using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WechatDraft.Windows;

public sealed record UiaRegionMap(Rectangle Window, Rectangle Header, Rectangle Editor, Rectangle MessageCapture,
    Rectangle? ScrollControl, uint Dpi);
public sealed record UiaRegionExport(UiaRegionMap Regions, byte[] PreviewPng);

internal static class UiaRegionPreview
{
    public static byte[] Render(Bitmap screenshot, UiaRegionMap regions)
    {
        if (screenshot.Size != regions.Window.Size) throw new InvalidDataException("UIA 截图与窗口尺寸不一致。");
        var scale = Math.Clamp(regions.Dpi / 96f, 1f, 2f);
        var gap = (int)(18 * scale);
        var legendWidth = (int)(300 * scale);
        using var result = new Bitmap(screenshot.Width + legendWidth, Math.Max(screenshot.Height, (int)(720 * scale)), PixelFormat.Format32bppArgb);
        result.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.FromArgb(247, 249, 248));
        // Draw physical pixels explicitly; bitmap DPI metadata must not rescale the background.
        graphics.DrawImage(screenshot, new Rectangle(Point.Empty, screenshot.Size), new Rectangle(Point.Empty, screenshot.Size), GraphicsUnit.Pixel);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Microsoft YaHei UI", 15 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var heading = new Font("Microsoft YaHei UI", 21 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var dark = new SolidBrush(Color.FromArgb(38, 53, 55));
        using var muted = new SolidBrush(Color.FromArgb(106, 121, 115));
        var x = screenshot.Width + gap;
        var textWidth = legendWidth - 2 * gap;
        graphics.DrawString("UIA 区域识别结果", heading, dark, new RectangleF(x, gap, textWidth, 62 * scale));
        var y = 86 * scale;

        void Box(Rectangle screen, Color color, bool dashed = false)
        {
            var local = new Rectangle(screen.X - regions.Window.X, screen.Y - regions.Window.Y, screen.Width, screen.Height);
            local.Intersect(new Rectangle(Point.Empty, screenshot.Size));
            if (local.Width < 1 || local.Height < 1) return;
            using var pen = new Pen(color, 3 * scale) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            graphics.DrawRectangle(pen, local.X, local.Y, Math.Max(0, local.Width - 1), Math.Max(0, local.Height - 1));
        }
        void Legend(string title, string explanation, Color color)
        {
            using var brush = new SolidBrush(color);
            graphics.FillRectangle(brush, x, y + 3 * scale, 14 * scale, 14 * scale);
            graphics.DrawString(title, font, dark, new RectangleF(x + 24 * scale, y, textWidth - 24 * scale, 30 * scale));
            graphics.DrawString(explanation, font, muted, new RectangleF(x, y + 32 * scale, textWidth, 64 * scale));
            y += 103 * scale;
        }

        // The green screenshot range contains the title; it is not a UIA message-list text export.
        Box(regions.MessageCapture, Color.SeaGreen);
        if (regions.ScrollControl is { } scroll) Box(scroll, Color.DarkCyan, dashed: true);
        Box(regions.Editor, Color.MediumVioletRed);
        Box(regions.Header, Color.DarkOrange);
        Box(regions.Window, Color.RoyalBlue);
        Legend("蓝框：微信窗口", "UIA 根窗口的实际边界。", Color.RoyalBlue);
        Legend("橙框：聊天标题", "UIA 识别的标题文字控件。", Color.DarkOrange);
        Legend("紫框：输入编辑区", "UIA 能读取草稿的编辑控件。", Color.MediumVioletRed);
        Legend("绿框：聊天截图范围", "程序按标题、窗口及输入框位置计算；包含标题和消息。", Color.SeaGreen);
        Legend("青色虚线：滚动控件", regions.ScrollControl is null ? "当前未定位到唯一可滚动控件，因此不画此框。" : "UIA 的滚动控件边界，可用于补充历史。", Color.DarkCyan);
        graphics.DrawString("坐标来自当前 UIA 定位；没有调用视觉模型。", font, muted,
            new RectangleF(x, y + 10 * scale, textWidth, 70 * scale));
        using var stream = new MemoryStream(); result.Save(stream, ImageFormat.Png); return stream.ToArray();
    }
}
