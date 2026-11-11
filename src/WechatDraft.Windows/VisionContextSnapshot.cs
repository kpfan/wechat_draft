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
        if (layout.Header is null || layout.Messages is null || layout.Input is null)
            throw new InvalidDataException("缺少消息区裁剪边界，未上传截图。");
        var header = frame.ToClientRectangle(layout.Header);
        var messages = frame.ToClientRectangle(layout.Messages);
        var input = frame.ToClientRectangle(layout.Input);
        if (messages.Top < header.Bottom || messages.Bottom > input.Top)
            throw new InvalidDataException("消息区与标题或输入面板重叠，未上传截图。");
        using var crop = image.Clone(messages, PixelFormat.Format32bppArgb);
        using var stream = new MemoryStream();
        crop.Save(stream, ImageFormat.Png);
        return new($"vision_context_{index}", stream.ToArray());
    }

    public void Dispose() { _image?.Dispose(); _image = null; }
}
