using System;
using System.Drawing;
using System.IO;
using WechatDraft.Core;

namespace WechatDraft.Windows;

// v0 reference: match inside the selected window, then add its screen origin exactly once.
// Screenshot and template coordinates are physical pixels; DPI is not applied a second time.
internal sealed class VisionWindowFrame
{
    public Rectangle Bounds { get; }
    public uint Dpi { get; }

    public VisionWindowFrame(Rectangle bounds, uint dpi)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || dpi == 0)
            throw new InvalidDataException("微信窗口尺寸或显示缩放无效。");
        Bounds = bounds; Dpi = dpi;
    }

    public bool Matches(Rectangle bounds, uint dpi) => Bounds == bounds && Dpi == dpi;

    public void ValidateImageSize(Size size)
    {
        if (size != Bounds.Size)
            throw new InvalidDataException("截图尺寸与所选微信窗口不一致，已停止定位。");
    }

    public Point ToClientPoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= 1000 || y >= 1000)
            throw new InvalidDataException("操作坐标超出所选微信窗口，未继续操作。");
        return new((int)Math.Floor(x * Bounds.Width / 1000), (int)Math.Floor(y * Bounds.Height / 1000));
    }

    public Point ToScreenPoint(double x, double y)
    {
        var local = ToClientPoint(x, y);
        return new(checked(Bounds.Left + local.X), checked(Bounds.Top + local.Y));
    }

    public Rectangle ToClientRectangle(VisionBox box)
    {
        if (!double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height) ||
            box.X < 0 || box.Y < 0 || box.Width <= 0 || box.Height <= 0 || box.Right > 1000 || box.Bottom > 1000)
            throw new InvalidDataException("区域坐标超出所选微信窗口，未继续操作。");
        var result = Rectangle.FromLTRB((int)Math.Ceiling(box.X * Bounds.Width / 1000),
            (int)Math.Ceiling(box.Y * Bounds.Height / 1000), (int)Math.Floor(box.Right * Bounds.Width / 1000),
            (int)Math.Floor(box.Bottom * Bounds.Height / 1000));
        if (result.Width <= 0 || result.Height <= 0)
            throw new InvalidDataException("区域在当前截图中没有可用像素，未继续操作。");
        return result;
    }
}
