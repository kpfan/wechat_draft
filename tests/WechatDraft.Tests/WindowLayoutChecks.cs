using System.Drawing;
using WechatDraft.Core;
using WechatDraft.Windows;

internal static class WindowLayoutChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Window layout preserves logical size across DPI changes and negative monitor origins", () =>
        {
            var original = new Rectangle(-1800, 100, 1600, 1200);
            var saved = WindowLayoutRules.Capture(original, 192);
            Equal(800d, saved.WidthDip); Equal(600d, saved.HeightDip); Equal(false, saved.Maximized);
            var workArea = new Rectangle(-2560, -200, 2560, 1800);
            Equal(original, WindowLayoutRules.Restore(saved, workArea, 192));
            Equal(new Rectangle(-1800, 100, 1200, 900), WindowLayoutRules.Restore(saved, workArea, 144));
        });
        yield return ("Removed monitors and small high-DPI desktops cannot restore a window off screen", () =>
        {
            var saved = new WindowLayoutSettings { Left = 10000, Top = 10000, WidthDip = 3000, HeightDip = 2000, Maximized = false };
            var workArea = new Rectangle(-1280, 0, 1280, 680);
            Equal(workArea, WindowLayoutRules.Restore(saved, workArea, 192));
            var tiny = WindowLayoutRules.Restore(saved with { WidthDip = 100, HeightDip = 100 }, workArea, 192);
            Equal(workArea, tiny);
        });
        yield return ("Invalid legacy window dimensions recover to visible bounds", () =>
        {
            var workArea = new Rectangle(0, 0, 1920, 1040);
            foreach (var size in new[] { double.NaN, double.PositiveInfinity, 0, -1 })
            {
                var bounds = WindowLayoutRules.Restore(new() { WidthDip = size, HeightDip = size, Left = int.MinValue, Top = int.MaxValue }, workArea, 192);
                if (!workArea.Contains(bounds) || bounds.Width <= 0 || bounds.Height <= 0) throw new Exception("Window escaped the available desktop.");
            }
            var first = WindowLayoutRules.Restore(null, workArea, 192);
            if (!workArea.Contains(first) || first.Width < 1400 || first.Height < 900) throw new Exception("High-DPI default window remains too small.");
        });
    }

    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
}
