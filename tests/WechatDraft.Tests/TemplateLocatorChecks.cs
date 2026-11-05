using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WechatDraft.Windows;

internal static class TemplateLocatorChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Local toolbar anchors follow DPI-scaled templates within the selected frame", () =>
        {
            foreach (var dpi in new uint[] { 96, 144, 192 })
            {
                using var frame = Fixture(dpi, false);
                var result = WechatTemplateLocator.Locate(frame, dpi, default)
                    ?? throw new Exception("Known templates were not located at DPI " + dpi);
                if (result.TopScore < 0.80 || result.BottomScore < 0.80 || result.Scale != dpi / 192d)
                    throw new Exception("DPI scale or confidence was not preserved.");
                Near(700, result.TopToolbar.X * frame.Width / 1000);
                Near(40, result.TopToolbar.Y * frame.Height / 1000);
                Near(350, result.BottomToolbar.X * frame.Width / 1000);
                Near(610, result.BottomToolbar.Y * frame.Height / 1000);
            }
        });
        yield return ("Local toolbar matching rejects low detail and ambiguous duplicate anchors", () =>
        {
            using var blank = new Bitmap(1100, 800);
            using (var graphics = Graphics.FromImage(blank)) graphics.Clear(Color.FromArgb(31, 31, 31));
            if (WechatTemplateLocator.Locate(blank, 192, default) is not null)
                throw new Exception("A flat image cannot establish toolbar identity.");
            using var duplicate = Fixture(192, true);
            if (WechatTemplateLocator.Locate(duplicate, 192, default) is not null)
                throw new Exception("Two matching toolbars must be treated as ambiguous.");
            if (WechatTemplateLocator.Locate(blank, 0, default) is not null ||
                WechatTemplateLocator.Locate(blank, 768, default) is not null)
                throw new Exception("Unsupported DPI must not guess a scale.");
        });
        yield return ("Local toolbar matching responds to cancellation before using a candidate", () =>
        {
            using var frame = Fixture(192, false);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { WechatTemplateLocator.Locate(frame, 192, cancellation.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("A cancelled local search returned a candidate.");
        });
        yield return ("Toolbars in another window cannot establish anchors in the selected crop", () =>
        {
            using var otherWindow = Fixture(192, false);
            if (WechatTemplateLocator.Locate(otherWindow, 192, default) is null)
                throw new Exception("The other-window fixture must contain valid toolbar matches.");
            using var desktop = new Bitmap(2200, 800, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(desktop))
            {
                graphics.Clear(Color.FromArgb(31, 31, 31));
                graphics.DrawImage(otherWindow, new Rectangle(1100, 0, 1100, 800), new Rectangle(0, 0, 1100, 800), GraphicsUnit.Pixel);
            }
            using var selectedWindow = desktop.Clone(new Rectangle(0, 0, 1000, 800), PixelFormat.Format32bppArgb);
            if (WechatTemplateLocator.Locate(selectedWindow, 192, default) is not null)
                throw new Exception("A toolbar outside the selected window influenced its anchors.");
        });
    }

    private static Bitmap Fixture(uint dpi, bool duplicate)
    {
        var result = new Bitmap(1100, 800, PixelFormat.Format32bppArgb); result.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(result);
        graphics.Clear(Color.FromArgb(31, 31, 31));
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.InterpolationMode = dpi == 192 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        void Place(string name, int x, int y)
        {
            using var stream = typeof(WechatTemplateLocator).Assembly.GetManifestResourceStream("WechatDraft.Windows.Templates." + name)
                ?? throw new Exception("Missing toolbar test resource.");
            using var image = new Bitmap(stream);
            var width = (int)Math.Round(image.Width * dpi / 192d); var height = (int)Math.Round(image.Height * dpi / 192d);
            graphics.DrawImage(image, new Rectangle(x, y, width, height),
                new Rectangle(0, 0, image.Width, image.Height), GraphicsUnit.Pixel);
        }
        Place("wechat_top_template.png", 700, 40);
        Place("wechat_bottom_template.png", 350, 610);
        if (duplicate) Place("wechat_top_template.png", 700, 150);
        return result;
    }

    private static void Near(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 2) throw new Exception($"Expected anchor near {expected}; got {actual}.");
    }
}
