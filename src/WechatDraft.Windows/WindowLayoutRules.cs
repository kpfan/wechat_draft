using System;
using System.Drawing;
using System.IO;
using WechatDraft.Core;

namespace WechatDraft.Windows;

public static class WindowLayoutRules
{
    public static uint WindowDpi(nint hwnd)
    {
        var dpi = Native.GetDpiForWindow(hwnd);
        return dpi == 0 ? 96 : dpi;
    }

    public static Rectangle Restore(WindowLayoutSettings? saved, Rectangle workArea, uint dpi)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new InvalidDataException("显示器工作区无效。");
        var scale = Scale(dpi);
        var width = Extent(saved?.WidthDip ?? 840, 840, 720, workArea.Width, scale, saved is null);
        var height = Extent(saved?.HeightDip ?? 740, 740, 520, workArea.Height, scale, saved is null);
        var left = saved?.Left ?? workArea.Left + (workArea.Width - width) / 2;
        var top = saved?.Top ?? workArea.Top + (workArea.Height - height) / 2;
        return new(Math.Clamp(left, workArea.Left, workArea.Right - width),
            Math.Clamp(top, workArea.Top, workArea.Bottom - height), width, height);
    }

    public static WindowLayoutSettings Capture(Rectangle normalBounds, uint dpi)
    {
        if (normalBounds.Width <= 0 || normalBounds.Height <= 0) throw new InvalidDataException("普通窗口尺寸无效。");
        return new() { Left = normalBounds.Left, Top = normalBounds.Top,
            WidthDip = normalBounds.Width / Scale(dpi), HeightDip = normalBounds.Height / Scale(dpi), Maximized = false };
    }

    private static double Scale(uint dpi) => dpi is >= 48 and <= 960 ? dpi / 96d : 1;
    private static int Extent(double value, double fallback, double minimum, int available, double scale, bool firstRun)
    {
        if (!double.IsFinite(value) || value <= 0) value = fallback;
        var maximum = firstRun ? available * 0.9 : available;
        return (int)Math.Round(Math.Clamp(value * scale, Math.Min(minimum * scale, maximum), maximum));
    }
}
