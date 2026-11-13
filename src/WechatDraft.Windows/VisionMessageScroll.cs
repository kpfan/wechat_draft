using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace WechatDraft.Windows;

internal static class VisionMessageScroll
{
    internal static Point BottomThumbPoint(Rectangle input, uint dpi) =>
        new(input.Right - (int)System.Math.Ceiling(dpi / 96d),
            input.Top - (int)System.Math.Ceiling(16 * dpi / 96d));

    internal static int EstimateDownwardShift(VisionRegionSnapshot before, VisionRegionSnapshot after)
    {
        if (before.Width != after.Width || before.Height != after.Height) return 0;
        var beforeRows = UniqueRows(before);
        var afterRows = UniqueRows(after);
        var offsets = new Dictionary<int, int>();
        foreach (var (signature, y) in beforeRows)
        {
            if (y < 0 || !afterRows.TryGetValue(signature, out var newY) || newY <= y) continue;
            var offset = newY - y;
            offsets[offset] = offsets.GetValueOrDefault(offset) + 1;
        }
        var best = offsets.OrderByDescending(pair => pair.Value).Take(2).ToArray();
        return best.Length > 0 && best[0].Value >= 8 && (best.Length == 1 || best[0].Value >= 2 * best[1].Value)
            ? best[0].Key : 0;
    }

    private static Dictionary<ulong, int> UniqueRows(VisionRegionSnapshot image)
    {
        var rows = new Dictionary<ulong, int>();
        var step = System.Math.Max(1, image.Width / 96);
        var stride = image.Data.Length / image.Height;
        for (var y = 0; y < image.Height; y++)
        {
            var hash = 14695981039346656037UL;
            var row = y * stride;
            var first = Pixel(image.Data, row);
            var varied = false;
            for (var x = 0; x < image.Width; x += step)
            {
                var pixel = Pixel(image.Data, row + x * 4);
                varied |= pixel != first;
                hash = unchecked((hash ^ pixel) * 1099511628211UL);
            }
            if (varied && !rows.TryAdd(hash, y)) rows[hash] = -1;
        }
        return rows;
    }

    private static uint Pixel(byte[] data, int offset) =>
        (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16);
}
