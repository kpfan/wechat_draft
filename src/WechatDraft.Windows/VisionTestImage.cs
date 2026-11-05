using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;

namespace WechatDraft.Windows;

public static class VisionTestImage
{
    private static Bitmap CreateBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height);
        // Point-sized fonts must not inherit the desktop DPI and overflow the fixed pixel canvas.
        bitmap.SetResolution(96, 96);
        return bitmap;
    }

    public static (byte[] Png, string DisplayName) CreateWindow(bool selected = false, bool empty = false, string? displayName = null,
        bool bottomToolbar = false, bool imageDraft = false)
    {
        var name = displayName ?? "测试联系人W" + RandomNumberGenerator.GetInt32(1000, 9999);
        using var bitmap = CreateBitmap(1320, 900);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            using var font = new Font("Microsoft YaHei", 18);
            using var title = new Font("Microsoft YaHei", name.Length <= 2 ? 18 : 26, FontStyle.Bold);
            g.FillRectangle(Brushes.WhiteSmoke, 0, 0, 320, 900);
            g.DrawString("微信 · 合成测试", font, Brushes.Gray, 22, 20);
            g.DrawString("合成联系人甲", font, Brushes.Black, 22, 120);
            g.DrawString("合成联系人乙", font, Brushes.Black, 22, 210);
            g.DrawString(name, title, Brushes.Black, 345, 20);
            g.DrawString("聊天信息 · 单聊", font, Brushes.DimGray, 1080, 30);
            g.DrawLine(Pens.LightGray, 320, 90, 1319, 90);
            g.FillRectangle(Brushes.Gainsboro, 350, 145, 570, 75);
            g.DrawString("最近一切都好，谢谢关心。", font, Brushes.Black, 365, 165);
            g.FillRectangle(Brushes.LightGreen, 660, 320, 610, 80);
            g.DrawString("周老师，最近辛苦了，您也多保重。", font, Brushes.Black, 675, 340);
            g.DrawLine(Pens.Gray, 320, 635, 1319, 635);
            g.DrawString("表情    文件    截图", font, Brushes.Gray, 345, bottomToolbar ? 848 : 650);
            var textTop = bottomToolbar ? 665 : 720;
            if (imageDraft)
            {
                g.FillRectangle(Brushes.LightSkyBlue, 350, textTop, 240, 120);
                g.FillPolygon(Brushes.ForestGreen, [new Point(350, textTop + 120), new Point(450, textTop + 40), new Point(590, textTop + 120)]);
                g.FillEllipse(Brushes.Gold, 530, textTop + 10, 30, 30);
                g.DrawRectangle(Pens.Gray, 350, textTop, 240, 120);
                g.DrawString("图片附件", font, Brushes.Black, 360, textTop + 80);
                g.DrawLine(Pens.Black, 605, textTop, 605, textTop + 120);
            }
            else if (empty) g.DrawLine(Pens.Black, 350, textTop, 350, textTop + 32);
            else
            {
                if (selected) g.FillRectangle(Brushes.LightBlue, 349, textTop - 4, 550, 38);
                g.DrawString("这是已有测试草稿，保留末尾空格。  ", font, Brushes.Black, 350, textTop);
            }
            g.FillRectangle(Brushes.Gainsboro, 1170, 835, 120, 45);
            g.DrawString("发送", font, Brushes.Black, 1190, 841);
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        return (stream.ToArray(), name);
    }

    public static (byte[] Png, string DisplayName) CreateConversation()
    {
        var name = "测试联系人W" + RandomNumberGenerator.GetInt32(1000, 9999);
        using var bitmap = CreateBitmap(980, 460);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            using var title = new Font("Microsoft YaHei", 32, FontStyle.Bold);
            using var body = new Font("Microsoft YaHei", 22);
            g.DrawString(name, title, Brushes.Black, new PointF(28, 18));
            g.DrawLine(Pens.Gray, 0, 95, bitmap.Width, 95);
            g.FillRectangle(Brushes.Gainsboro, 25, 135, 500, 80);
            g.DrawString("最近一切都好，谢谢关心。", body, Brushes.Black, new PointF(40, 150));
            g.FillRectangle(Brushes.LightGreen, 260, 270, 690, 90);
            g.DrawString("周老师，最近辛苦了，您也多保重。", body, Brushes.Black, new PointF(275, 290));
            g.DrawString("合成测试界面，无真实联系人或聊天内容", body, Brushes.DimGray, new PointF(28, 395));
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        return (stream.ToArray(), name);
    }

    public static (byte[] Png, string Code) Create()
    {
        var code = "W" + RandomNumberGenerator.GetInt32(1000, 9999);
        using var bitmap = CreateBitmap(440, 180);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White); using var font = new Font("Arial", 55, FontStyle.Bold);
            g.DrawString(code, font, Brushes.DarkSlateBlue, new PointF(70, 45));
        }
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png);
        return (stream.ToArray(), code);
    }
}
