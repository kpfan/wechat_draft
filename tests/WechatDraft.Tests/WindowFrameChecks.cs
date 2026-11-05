using System.Drawing;
using WechatDraft.Core;
using WechatDraft.Windows;

internal static class WindowFrameChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Window-local coordinates add the selected screen origin exactly once", () =>
        {
            var box = new VisionBox { X = 250, Y = 200, Width = 500, Height = 400 };
            foreach (var origin in new[] { new Point(0, 0), new Point(450, 240), new Point(-1800, -900) })
            {
                foreach (var dpi in new uint[] { 96, 144, 192 })
                {
                    var frame = new VisionWindowFrame(new Rectangle(origin, new Size(1200, 800)), dpi);
                    Equal(new Point(300, 320), frame.ToClientPoint(250, 400));
                    Equal(new Point(origin.X + 300, origin.Y + 320), frame.ToScreenPoint(250, 400));
                    Equal(new Rectangle(300, 160, 600, 320), frame.ToClientRectangle(box));
                    frame.ValidateImageSize(new Size(1200, 800));
                }
            }
        });
        yield return ("Frozen capture geometry rejects moved, resized and DPI-changed windows", () =>
        {
            var bounds = new Rectangle(-1500, 100, 1100, 800);
            var frame = new VisionWindowFrame(bounds, 192);
            if (!frame.Matches(bounds, 192) || frame.Matches(bounds with { X = -1499 }, 192) ||
                frame.Matches(bounds with { Width = 1099 }, 192) || frame.Matches(bounds, 144) || frame.Matches(bounds, 0))
                throw new Exception("A stale window mapping was accepted.");
            Reject(() => frame.ValidateImageSize(new Size(2200, 800)));
            Reject(() => frame.ValidateImageSize(new Size(1100, 799)));
            Reject(() => new VisionWindowFrame(bounds, 0));
        });
        yield return ("Coordinate conversion rejects off-window and nonfinite action targets", () =>
        {
            var frame = new VisionWindowFrame(new Rectangle(400, 300, 1000, 600), 192);
            Reject(() => frame.ToScreenPoint(-1, 100));
            Reject(() => frame.ToScreenPoint(1000, 100));
            Reject(() => frame.ToScreenPoint(100, double.NaN));
            Reject(() => frame.ToClientRectangle(new() { X = 990, Y = 100, Width = 20, Height = 20 }));
            Reject(() => frame.ToClientRectangle(new() { X = 100, Y = 100, Width = double.PositiveInfinity, Height = 20 }));
            Reject(() => frame.ToClientRectangle(new() { X = 100, Y = 100, Width = 0.1, Height = 0.1 }));
        });
    }

    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Invalid window geometry was accepted.");
    }
}
