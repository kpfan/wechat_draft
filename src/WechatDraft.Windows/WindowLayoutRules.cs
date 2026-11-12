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
        var width = Extent(saved?.WidthDip, WindowLayoutSettings.DefaultWidthDip, 1280, workArea.Width, scale);
        var height = Extent(saved?.HeightDip, WindowLayoutSettings.DefaultHeightDip, 1200, workArea.Height, scale);
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
    private static int Extent(double? saved, double fallback, double minimum, int available, double scale)
    {
        var preferredLimit = Math.Max(1, available * .9);
        if (saved is not { } value || !double.IsFinite(value) || value <= 0)
            return (int)Math.Round(Math.Min(fallback * scale, preferredLimit));
        var minimumOnScreen = Math.Min(minimum * scale, preferredLimit);
        return (int)Math.Round(Math.Clamp(value * scale, minimumOnScreen, available));
    }
}
