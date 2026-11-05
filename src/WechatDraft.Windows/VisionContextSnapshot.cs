using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using WechatDraft.Core;

namespace WechatDraft.Windows;

// Own the startup image so later desktop changes cannot enter a read-only task.
internal sealed class VisionContextSnapshot(Bitmap source) : IDisposable
{
    private Bitmap? _image = (Bitmap)source.Clone();

    public Bitmap CopyImage() => (Bitmap)(_image ?? throw new ObjectDisposedException(nameof(VisionContextSnapshot))).Clone();

    public Capture Capture(VisionWindowFrame frame, VisionLayout layout, int index) =>
        CreateCapture(_image ?? throw new ObjectDisposedException(nameof(VisionContextSnapshot)), frame, layout, index);

    internal static Capture CreateCapture(Bitmap image, VisionWindowFrame frame, VisionLayout layout, int index)
    {
        frame.ValidateImageSize(image.Size);
        var header = frame.ToClientRectangle(layout.Header!);
        var messages = frame.ToClientRectangle(layout.Messages!);
        var scale = Math.Min(4d, 1800d / header.Width);
        var headerWidth = (int)(header.Width * scale);
        var headerHeight = (int)(header.Height * scale);
        using var composite = new Bitmap(Math.Max(messages.Width, headerWidth), messages.Height + headerHeight + 16);
        using (var graphics = Graphics.FromImage(composite))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(image, new Rectangle(0, 0, headerWidth, headerHeight), header, GraphicsUnit.Pixel);
            graphics.DrawImage(image, new Rectangle(0, headerHeight + 16, messages.Width, messages.Height), messages, GraphicsUnit.Pixel);
        }
        using var stream = new MemoryStream();
        composite.Save(stream, ImageFormat.Png);
        return new($"vision_context_{index}", stream.ToArray());
    }

    public void Dispose() { _image?.Dispose(); _image = null; }
}
