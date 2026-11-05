using System.Drawing;
using System.Runtime.InteropServices;
using WechatDraft.Windows;

internal static class VisionImageChecks
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    public static void Run()
    {
        byte[]? referenceWindow = null;
        foreach (var context in new nint[] { -1, -4 })
        {
            var previous = SetThreadDpiAwarenessContext(context);
            if (previous == 0) throw new InvalidOperationException("Could not select the test DPI context.");
            try
            {
                var (png, _) = VisionTestImage.Create();
                using var stream = new MemoryStream(png);
                using var bitmap = new Bitmap(stream);
                var ink = false;
                for (var y = 0; y < bitmap.Height; y++)
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        if (bitmap.GetPixel(x, y).R >= 220) continue;
                        ink = true;
                        if (x < 20 || y < 20 || x >= bitmap.Width - 20 || y >= bitmap.Height - 20)
                            throw new Exception("The vision test code is clipped or lacks a readable margin.");
                    }
                if (!ink) throw new Exception("The vision test image has no visible code.");

                var window = VisionTestImage.CreateWindow(selected: true, displayName: "测试").Png;
                if (referenceWindow is not null && !referenceWindow.AsSpan().SequenceEqual(window))
                    throw new Exception("The synthetic window changes with desktop DPI awareness.");
                referenceWindow = window;

                using var conversationStream = new MemoryStream(VisionTestImage.CreateConversation().Png);
                using var conversation = new Bitmap(conversationStream);
                if (Math.Abs(conversation.HorizontalResolution - 96) > 0.1 ||
                    Math.Abs(conversation.VerticalResolution - 96) > 0.1)
                    throw new Exception("The synthetic conversation inherits desktop DPI.");
            }
            finally { SetThreadDpiAwarenessContext(previous); }
        }
    }
}
