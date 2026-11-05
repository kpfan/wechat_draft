using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WechatDraft.Core;

namespace WechatDraft.Windows;

internal sealed record VisionRegionSnapshot(int Width, int Height, byte[] Data)
{
    public static VisionRegionSnapshot CaptureInput(Bitmap bitmap, VisionLayout layout)
    {
        var box = layout.Input ?? throw new InvalidOperationException("输入面板尚未定位。");
        var region = Rectangle.FromLTRB((int)Math.Ceiling(box.X * bitmap.Width / 1000),
            (int)Math.Ceiling(box.Y * bitmap.Height / 1000), (int)Math.Floor(box.Right * bitmap.Width / 1000),
            (int)Math.Floor(box.Bottom * bitmap.Height / 1000));
        return Capture(bitmap, region);
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

    public bool Matches(VisionRegionSnapshot after, bool allowCaret)
    {
        if (Width != after.Width || Height != after.Height) return false;
        if (Data.AsSpan().SequenceEqual(after.Data)) return true;
        if (!allowCaret) return false;
        var minX = Width; var maxX = -1; var minY = Height; var maxY = -1; var changed = 0;
        for (var i = 0; i < Data.Length; i += 4)
            if (!Data.AsSpan(i, 4).SequenceEqual(after.Data.AsSpan(i, 4)))
            {
                var x = i / 4 % Width; var y = i / 4 / Width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); changed++;
            }
        return changed <= 144 && maxX - minX < 3 && maxY - minY < 48;
    }
}
