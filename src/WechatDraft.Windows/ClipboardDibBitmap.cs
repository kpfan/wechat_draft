using System;
using System.Drawing;
using System.IO;

namespace WechatDraft.Windows;

// Rebuild an unreadable synthesized CF_BITMAP from the independently copied CF_DIB.
// Original DIB/PNG and all other user payloads remain separately preserved.
internal static class ClipboardDibBitmap
{
    internal static (byte[] Bmp, long PixelBytes) Prepare(byte[] dib)
    {
        if (dib.Length < 40) throw new InvalidOperationException("剪贴板 DIB 图片头不完整。");
        var header = BitConverter.ToUInt32(dib, 0);
        var width = BitConverter.ToInt32(dib, 4); var height = BitConverter.ToInt32(dib, 8);
        var planes = BitConverter.ToUInt16(dib, 12); var bits = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToUInt32(dib, 16); var colors = BitConverter.ToUInt32(dib, 32);
        if (header is not (40 or 52 or 56 or 108 or 124) || width <= 0 || height == 0 || planes != 1 ||
            bits is not (1 or 4 or 8 or 16 or 24 or 32) || compression is not (0 or 3 or 6) || colors > 256)
            throw new InvalidOperationException("剪贴板 DIB 图片格式暂不支持安全备份。");
        var palette = colors > 0 ? colors : bits <= 8 ? 1u << bits : 0;
        var masks = header == 40 ? compression == 3 ? 12 : compression == 6 ? 16 : 0 : 0;
        var offset = checked((long)header + masks + palette * 4L);
        var pixels = checked(((width * (long)bits + 31) / 32) * 4 * Math.Abs((long)height));
        if (pixels <= 0 || pixels > NativeClipboardSnapshot.MaximumItemBytes || offset + pixels > dib.Length)
            throw new InvalidOperationException("剪贴板 DIB 图片数据不完整或超出备份上限。");
        var bmp = new byte[checked(dib.Length + 14)];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes((uint)bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(checked((uint)(offset + 14))).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return (bmp, pixels);
    }

    internal static nint Create(byte[] bmp)
    {
        using var stream = new MemoryStream(bmp, writable: false);
        using var image = new Bitmap(stream);
        return image.GetHbitmap();
    }
}
