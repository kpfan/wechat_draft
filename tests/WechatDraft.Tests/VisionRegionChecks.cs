using System.Drawing;
using WechatDraft.Core;
using WechatDraft.Windows;

internal static class VisionRegionChecks
{
    public static void CheckOutsideClick(VisionLayout layout)
    {
        using var bitmap = new Bitmap(1000, 1000);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        var input = VisionRegionSnapshot.CaptureInput(bitmap, layout);
        var click = VisionRules.ClickRegion(layout);
        var clickRect = Rectangle.FromLTRB((int)click.X, (int)click.Y, (int)click.Right, (int)click.Bottom);
        var clickPixels = VisionRegionSnapshot.Capture(bitmap, clickRect);
        // This change is in the complete input panel, beyond both the canvas and click box.
        var changed = new Rectangle((int)layout.Input!.Right - 24, (int)layout.Input.Y + 16, 12, 12);
        graphics.FillRectangle(Brushes.Black, changed);
        if (input.Matches(VisionRegionSnapshot.CaptureInput(bitmap, layout), true))
            throw new Exception("A change outside the click region went unnoticed.");
        if (!clickPixels.Matches(VisionRegionSnapshot.Capture(bitmap, clickRect), true))
            throw new Exception("The fixture did not isolate a change outside the click region.");
        graphics.Clear(Color.White);
        graphics.FillRectangle(Brushes.Black, 10, 10, 20, 20);
        if (!input.Matches(VisionRegionSnapshot.CaptureInput(bitmap, layout), true))
            throw new Exception("A change outside the input panel affected its snapshot.");
    }

    public static void CheckCaretAndResize(VisionLayout layout)
    {
        using var bitmap = new Bitmap(1000, 1000);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        var input = VisionRegionSnapshot.CaptureInput(bitmap, layout);
        graphics.FillRectangle(Brushes.Black, 480, 760, 1, 25);
        var caret = VisionRegionSnapshot.CaptureInput(bitmap, layout);
        if (!input.Matches(caret, true) || input.Matches(caret, false))
            throw new Exception("Caret tolerance was not preserved.");
        graphics.FillRectangle(Brushes.Black, 760, 800, 12, 12);
        if (input.Matches(VisionRegionSnapshot.CaptureInput(bitmap, layout), true))
            throw new Exception("A broader change was treated as caret blinking.");
        using var resized = new Bitmap(1200, 1000);
        if (input.Matches(VisionRegionSnapshot.CaptureInput(resized, layout), true))
            throw new Exception("A changed window size was treated as unchanged.");
    }
}
