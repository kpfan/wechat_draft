using System.Drawing;
using System.Net;
using System.Text.Json;
using WechatDraft.Core;
using WechatDraft.Windows;

internal static class OcrPrefillChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root, ExampleService examples, ISecretProtector protector)
    {
        LocalRepository Repository() => new(Path.Combine(root, "ocr-" + Guid.NewGuid().ToString("N")), protector);
        var configuration = new AppSettings { DemoMode = false, Scene = "生日", BaseUrl = "https://test.example/v1",
            AuthorizedOrigin = "https://test.example", Model = "fake", EnableLiveAppend = true };
        yield return ("Plain-text copy rejects embedded image objects carried alongside Unicode text", () =>
        {
            if (!NativeClipboardBackend.ContainsEmbeddedObject("<html><IMG src='x'></html>", false) ||
                !NativeClipboardBackend.ContainsEmbeddedObject(@"{\rtf1 text {\pict data}}", true) ||
                NativeClipboardBackend.ContainsEmbeddedObject("<html><b>文字</b></html>", false))
                throw new Exception("Mixed image/text payload detection failed.");
            return Task.CompletedTask;
        });
        yield return ("DIB bitmap recovery preserves image pixels for standard and V5 clipboard payloads", () =>
        {
            foreach (var header in new[] { 40, 124 })
            {
                var dib = new byte[header + 16];
                BitConverter.GetBytes(header).CopyTo(dib, 0); BitConverter.GetBytes(2).CopyTo(dib, 4); BitConverter.GetBytes(2).CopyTo(dib, 8);
                dib[12] = 1; dib[14] = 32;
                new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 255, 255, 255, 255 }.CopyTo(dib, header);
                var prepared = ClipboardDibBitmap.Prepare(dib);
                if (prepared.PixelBytes != 16) throw new Exception("Wrong DIB byte bound.");
                var bitmap = ClipboardDibBitmap.Create(prepared.Bmp);
                try
                {
                    using var image = Image.FromHbitmap(bitmap); using var pixels = new Bitmap(image);
                    if (image.Width != 2 || image.Height != 2 || pixels.GetPixel(0, 1).ToArgb() != Color.Red.ToArgb() ||
                        pixels.GetPixel(0, 0).ToArgb() != Color.Blue.ToArgb()) throw new Exception("DIB recovery changed image pixels.");
                }
                finally { Native.DeleteObject(bitmap); }
            }
            return Task.CompletedTask;
        });
        yield return ("DIB bitmap recovery rejects truncated and oversized images before allocating a bitmap", () =>
        {
            var baseline = new byte[44]; baseline[0] = 40; baseline[4] = 1; baseline[8] = 1; baseline[12] = 1; baseline[14] = 32;
            var truncated = baseline[..40];
            var huge = (byte[])baseline.Clone(); BitConverter.GetBytes(int.MaxValue).CopyTo(huge, 4);
            var compressed = (byte[])baseline.Clone(); compressed[16] = 5;
            foreach (var bad in new[] { new byte[2], truncated, huge, compressed })
            {
                try { ClipboardDibBitmap.Prepare(bad); }
                catch (InvalidOperationException) { continue; }
                throw new Exception("Unsafe DIB was accepted.");
            }
            return Task.CompletedTask;
        });
        yield return ("Visual layout recheck rejects an input divider moved inside the same window", () =>
        {
            var box = new VisionBox { X = 10, Y = 10, Width = 300, Height = 200 };
            var layout = new VisionLayout { Header = box, Input = box, Editor = box, Send = box, InputToolbar = box };
            if (!WechatLocalLayout.SameGeometry(layout, layout, 1100, 800) ||
                WechatLocalLayout.SameGeometry(layout, layout with { Editor = box with { Y = 20 } }, 1100, 800))
                throw new Exception("Changed local input geometry retained write permission.");
            return Task.CompletedTask;
        });
        yield return ("OCR supplies title to generation and permits one checked append without a model title", async () =>
        {
            var adapter = new FakeAdapter { IsDemo = false, TitleSource = "ocr", RequiresConversationCheck = true };
            var result = await new TaskCoordinator(Repository(), examples, protector).RunAsync(configuration, adapter, new FakeModel(), null, default);
            if (result!.GenerationStatus != "成功" || result.FillStatus != "已预填" || adapter.Appends != 1 ||
                result.ObservedDisplayName != adapter.DisplayName || result.ScreenshotRecipientMatched != true || result.LayoutApiCallCount != 0)
                throw new Exception("Model title remained mandatory or the checked OCR append failed.");
        });
        yield return ("A model cannot override the locally read OCR title or reject it with a conflicting name", async () =>
        {
            var adapter = new FakeAdapter { IsDemo = false, TitleSource = "ocr", RequiresConversationCheck = true };
            var result = await new TaskCoordinator(Repository(), examples, protector).RunAsync(configuration, adapter,
                new FakeModel { ObservedName = "其他名字(999)" }, null, default);
            if (result!.FillStatus != "已预填" || result.ObservedDisplayName != adapter.DisplayName || adapter.Appends != 1)
                throw new Exception("Untrusted model title replaced local OCR evidence.");
        });
        yield return ("Changed OCR state preserves generated text and does not create a paste intent", async () =>
        {
            var adapter = new FakeAdapter { IsDemo = false, TitleSource = "ocr", RequiresConversationCheck = true };
            var result = await new TaskCoordinator(Repository(), examples, protector).RunAsync(configuration, adapter,
                new FakeModel { BeforeReturn = () => adapter.Valid = false }, null, default);
            if (result!.GenerationStatus != "成功" || string.IsNullOrEmpty(result.Greeting) || adapter.Appends != 0 || result.AppendAttemptId is not null)
                throw new Exception("Changed title/draft wrote text or lost the generated candidate.");
        });
        yield return ("Unread OCR-mode draft remains read-only even when prefill is selected", async () =>
        {
            var adapter = new FakeAdapter { IsDemo = false, TitleSource = "ocr", OriginalTextRead = false, CanAutoAppend = false,
                RequiresConversationCheck = true };
            var result = await new TaskCoordinator(Repository(), examples, protector).RunAsync(configuration, adapter, new FakeModel(), null, default);
            if (result!.GenerationStatus != "成功" || adapter.Appends != 0 || result.FillStatus != "待手动处理")
                throw new Exception("An unread draft was promoted to a write.");
        });
        yield return ("OCR guard permanently rejects changed titles and exact whitespace changes", () =>
        {
            var guard = new OcrPrefillGuard("测试", " 原文\r\n");
            if (!guard.CheckTitle("测试") || !guard.CheckDraft(" 原文\r\n")) throw new Exception("Baseline rejected.");
            if (guard.CheckTitle("其他") || guard.CheckTitle("测试") || guard.TryIssuePaste()) throw new Exception("Changed title regained permission.");
            guard = new("测试", " 原文\r\n");
            if (guard.CheckDraft("原文\n") || guard.CheckDraft(" 原文\r\n") || guard.TryIssuePaste()) throw new Exception("Changed original regained permission.");
            return Task.CompletedTask;
        });
        yield return ("Concurrent attempts can issue the OCR paste at most once and cannot retry uncertainty", async () =>
        {
            var guard = new OcrPrefillGuard("测试", ""); var issued = 0;
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => { if (guard.TryIssuePaste()) Interlocked.Increment(ref issued); })));
            if (issued != 1 || !guard.PasteIssued || guard.TryIssuePaste()) throw new Exception("Uncertain paste replayed.");
        });
        yield return ("Local selection and empty-editor checks reject message selections and nonblank attachments", () =>
        {
            using var blank = new Bitmap(220, 160); using var selected = new Bitmap(220, 160); using var caret = new Bitmap(220, 160);
            foreach (var image in new[] { blank, selected, caret }) { using var g = Graphics.FromImage(image); g.Clear(Color.White); }
            var editor = new Rectangle(10, 50, 200, 100);
            using (var g = Graphics.FromImage(selected)) g.FillRectangle(Brushes.DodgerBlue, 30, 70, 80, 20);
            using (var g = Graphics.FromImage(caret)) g.FillRectangle(Brushes.Black, 30, 70, 1, 20);
            var blankEvidence = VisionEditorEvidence.InspectEmpty(blank, editor, 96);
            var caretEvidence = VisionEditorEvidence.InspectEmpty(caret, editor, 96);
            if (!VisionEditorEvidence.HasTextSelection(blank, selected, editor, 96) ||
                VisionEditorEvidence.ConfirmEmptyCaret(caretEvidence, blankEvidence) is null) throw new Exception("Valid local evidence was rejected.");
            if (VisionEditorEvidence.HasTextSelection(blank, selected, new(10, 0, 200, 40), 96) ||
                VisionEditorEvidence.ConfirmEmptyCaret(blankEvidence, blankEvidence) is not null) throw new Exception("Wrong-area or absent-caret evidence was accepted.");
            using var blueCaret = (Bitmap)blank.Clone();
            using (var g = Graphics.FromImage(blueCaret)) g.FillRectangle(Brushes.DodgerBlue, 30, 70, 1, 30);
            if (VisionEditorEvidence.HasTextSelection(blank, blueCaret, editor, 96)) throw new Exception("A blinking blue caret was treated as a text selection.");
            using (var g = Graphics.FromImage(blank)) g.FillRectangle(Brushes.Gray, 70, 80, 25, 25);
            using (var g = Graphics.FromImage(caret)) g.FillRectangle(Brushes.Gray, 70, 80, 25, 25);
            if (VisionEditorEvidence.InspectEmpty(caret, editor, 96).BlankCanvas) throw new Exception("An attachment-like object was called empty.");
            return Task.CompletedTask;
        });
        yield return ("Green WeChat selections validate the editor while green carets and wrong regions remain rejected", () =>
        {
            foreach (var dpi in new uint[] { 96, 144, 192, 288, 384 })
                foreach (var background in new[] { Color.White, Color.FromArgb(31, 31, 31) })
                {
                    var scale = dpi / 96d;
                    using var before = new Bitmap((int)(240 * scale), (int)(170 * scale));
                    using (var g = Graphics.FromImage(before)) g.Clear(background);
                    using var selected = (Bitmap)before.Clone(); using var caret = (Bitmap)before.Clone();
                    using (var g = Graphics.FromImage(selected))
                    {
                        using var brush = new SolidBrush(Color.FromArgb(47, 89, 66));
                        g.FillRectangle(brush, (int)(30 * scale), (int)(65 * scale), (int)(130 * scale), (int)(19 * scale));
                        g.FillRectangle(brush, (int)(30 * scale), (int)(90 * scale), (int)(70 * scale), (int)(19 * scale));
                    }
                    using (var g = Graphics.FromImage(caret))
                    {
                        using var brush = new SolidBrush(Color.FromArgb(7, 193, 96));
                        g.FillRectangle(brush, (int)(30 * scale), (int)(65 * scale), (int)(2 * scale), (int)(19 * scale));
                    }
                    var editor = new Rectangle((int)(10 * scale), (int)(50 * scale), (int)(220 * scale), (int)(110 * scale));
                    var outside = new Rectangle((int)(10 * scale), 0, (int)(220 * scale), (int)(40 * scale));
                    if (!VisionEditorEvidence.HasTextSelection(before, selected, editor, dpi) ||
                        VisionEditorEvidence.HasTextSelection(before, selected, outside, dpi) ||
                        VisionEditorEvidence.HasTextSelection(before, caret, editor, dpi) ||
                        VisionEditorEvidence.HasTextSelection(selected, selected, editor, dpi))
                        throw new Exception($"Green selection/caret/region checks failed at {dpi} DPI.");
                }
            return Task.CompletedTask;
        });
        yield return ("Stable colored carets prove empty editors without blinking across DPI and light or dark backgrounds", () =>
        {
            foreach (var dpi in new uint[] { 96, 144, 192, 288, 384 })
                foreach (var background in new[] { Color.White, Color.FromArgb(31, 31, 31) })
                    foreach (var color in new[] { Color.FromArgb(7, 193, 96), Color.DodgerBlue })
                    {
                        var scale = dpi / 96d;
                        using var image = new Bitmap((int)(220 * scale), (int)(160 * scale));
                        using (var g = Graphics.FromImage(image))
                        {
                            g.Clear(background); using var brush = new SolidBrush(color);
                            g.FillRectangle(brush, (int)(30 * scale), (int)(65 * scale), (int)(2 * scale), (int)(19 * scale));
                        }
                        var editor = new Rectangle((int)(10 * scale), (int)(50 * scale), (int)(200 * scale), (int)(100 * scale));
                        var first = VisionEditorEvidence.ConfirmEmptyCaret(VisionEditorEvidence.InspectEmpty(image, editor, dpi));
                        var second = VisionEditorEvidence.ConfirmEmptyCaret(VisionEditorEvidence.InspectEmpty(image, editor, dpi));
                        if (first is null || second is null || !VisionEditorEvidence.SameEmptyCaret(first, second))
                            throw new Exception($"Static caret rejected at {dpi} DPI.");
                    }
            return Task.CompletedTask;
        });
        yield return ("Blank screenshots and steady monochrome strokes cannot independently prove an empty editor", () =>
        {
            using var image = new Bitmap(220, 160); using var g = Graphics.FromImage(image); g.Clear(Color.White);
            var editor = new Rectangle(10, 50, 200, 100);
            var blank = VisionEditorEvidence.InspectEmpty(image, editor, 96);
            if (VisionEditorEvidence.ConfirmEmptyCaret(blank) is not null) throw new Exception("No visible focus evidence accepted.");
            g.FillRectangle(Brushes.Black, 30, 65, 1, 20);
            var stroke = VisionEditorEvidence.InspectEmpty(image, editor, 96);
            if (VisionEditorEvidence.ConfirmEmptyCaret(stroke) is not null || VisionEditorEvidence.ConfirmEmptyCaret(stroke, stroke) is not null)
                throw new Exception("Static text-like stroke accepted as an empty editor.");
            if (VisionEditorEvidence.ConfirmEmptyCaret(stroke, blank) is null || VisionEditorEvidence.ConfirmEmptyCaret(blank, stroke) is null)
                throw new Exception("Blinking monochrome caret rejected.");
            return Task.CompletedTask;
        });
        yield return ("Empty-editor endpoint checks reject spaces or newlines that move the caret and reject attachments", () =>
        {
            var editor = new Rectangle(10, 50, 200, 100);
            using var home = new Bitmap(220, 160);
            using (var g = Graphics.FromImage(home)) { g.Clear(Color.White); g.FillRectangle(Brushes.LimeGreen, 30, 60, 2, 20); }
            var first = VisionEditorEvidence.InspectEmpty(home, editor, 96);
            foreach (var point in new[] { new Point(36, 60), new Point(30, 70) })
            {
                using var end = new Bitmap(220, 160);
                using (var g = Graphics.FromImage(end)) { g.Clear(Color.White); g.FillRectangle(Brushes.LimeGreen, point.X, point.Y, 2, 20); }
                var second = VisionEditorEvidence.InspectEmpty(end, editor, 96);
                if (VisionEditorEvidence.SameEmptyCaret(first, second)) throw new Exception("Caret movement discarded invisible draft content.");
            }
            using (var g = Graphics.FromImage(home)) g.FillRectangle(Brushes.Gray, 160, 100, 24, 24);
            if (VisionEditorEvidence.InspectEmpty(home, editor, 96).BlankCanvas) throw new Exception("An object outside the click point was ignored.");
            return Task.CompletedTask;
        });
        yield return ("Input monitoring allows a scaled four-pixel caret but rejects broader draft changes", () =>
        {
            using var image = new Bitmap(320, 180); using var g = Graphics.FromImage(image); g.Clear(Color.FromArgb(31, 31, 31));
            var area = new Rectangle(0, 0, image.Width, image.Height);
            var baseline = VisionRegionSnapshot.Capture(image, area);
            g.FillRectangle(Brushes.LimeGreen, 30, 30, 4, 37);
            var caret = VisionRegionSnapshot.Capture(image, area);
            if (!baseline.Matches(caret, true, 192) || baseline.Matches(caret, true, 96) || baseline.Matches(caret, false, 192))
                throw new Exception("DPI-specific caret tolerance failed.");
            g.FillRectangle(Brushes.White, 100, 40, 24, 24);
            if (baseline.Matches(VisionRegionSnapshot.Capture(image, area), true, 192)) throw new Exception("A draft change was ignored.");
            return Task.CompletedTask;
        });
        yield return ("Known WeChat empty-input hint is ignored only with independent caret evidence across DPI", () =>
        {
            foreach (var dpi in new uint[] { 96, 144, 192, 288, 384 })
            {
                var scale = dpi / 96d;
                using var image = new Bitmap((int)(240 * scale), (int)(160 * scale));
                using var hint = WechatEmptyPlaceholder.Load(dpi);
                using (var g = Graphics.FromImage(image))
                {
                    g.Clear(Color.FromArgb(31, 31, 31));
                    g.FillRectangle(Brushes.LimeGreen, (int)(30 * scale), (int)(65 * scale), (int)(2 * scale), (int)(19 * scale));
                    g.DrawImage(hint, new Rectangle((int)(34 * scale), (int)(69 * scale), hint.Width, hint.Height),
                        new Rectangle(0, 0, hint.Width, hint.Height), GraphicsUnit.Pixel);
                }
                var editor = new Rectangle((int)(10 * scale), (int)(50 * scale), (int)(220 * scale), (int)(100 * scale));
                var evidence = VisionEditorEvidence.InspectEmpty(image, editor, dpi);
                if (evidence.Placeholder is null || VisionEditorEvidence.ConfirmEmptyCaret(evidence) is null)
                    throw new Exception($"Known hint or caret rejected at {dpi} DPI.");
            }
            return Task.CompletedTask;
        });
        yield return ("Known hints do not prove focus or hide real text, misplaced hints, or attachments", () =>
        {
            var editor = new Rectangle(10, 50, 220, 100);
            using var hint = WechatEmptyPlaceholder.Load(96);
            using var image = new Bitmap(240, 160);
            void PaintHint(int x = 34)
            {
                using var g = Graphics.FromImage(image); g.Clear(Color.FromArgb(31, 31, 31));
                g.DrawImage(hint, new Rectangle(x, 69, hint.Width, hint.Height), new Rectangle(0, 0, hint.Width, hint.Height), GraphicsUnit.Pixel);
            }
            PaintHint();
            if (VisionEditorEvidence.ConfirmEmptyCaret(VisionEditorEvidence.InspectEmpty(image, editor, 96)) is not null)
                throw new Exception("A placeholder without caret proved input focus.");
            using (var g = Graphics.FromImage(image))
            {
                g.FillRectangle(Brushes.LimeGreen, 30, 65, 2, 19); g.FillRectangle(Brushes.Gray, 180, 105, 25, 25);
            }
            if (VisionEditorEvidence.InspectEmpty(image, editor, 96).BlankCanvas) throw new Exception("Hint masked an attachment.");
            PaintHint(90);
            if (VisionEditorEvidence.InspectEmpty(image, editor, 96).BlankCanvas) throw new Exception("Misplaced hint was accepted.");
            PaintHint();
            for (var y = 69; y < 69 + hint.Height; y++)
                for (var x = 34; x < 34 + hint.Width; x++)
                    if (image.GetPixel(x, y).B > 46) image.SetPixel(x, y, Color.White);
            if (VisionEditorEvidence.InspectEmpty(image, editor, 96).BlankCanvas) throw new Exception("Actual bright text was treated as a placeholder.");
            using (var g = Graphics.FromImage(image))
            {
                g.Clear(Color.FromArgb(31, 31, 31)); using var brush = new SolidBrush(Color.FromArgb(75, 75, 75));
                using var font = new Font("Microsoft YaHei UI", 14); g.DrawString("这是我的草稿", font, brush, 34, 65);
            }
            if (VisionEditorEvidence.InspectEmpty(image, editor, 96).BlankCanvas) throw new Exception("Arbitrary gray text was ignored.");
            return Task.CompletedTask;
        });
        yield return ("Empty hint fading preserves a validated baseline without hiding real text or changes outside the known hint", () =>
        {
            foreach (var dpi in new uint[] { 96, 144, 192 })
            {
                var scale = dpi / 96d; using var image = new Bitmap((int)(240 * scale), (int)(160 * scale));
                using var hint = WechatEmptyPlaceholder.Load(dpi);
                var editor = new Rectangle(0, (int)(48 * scale), image.Width, (int)(96 * scale));
                var layout = new VisionLayout { Input = new() { X = 0, Y = 0, Width = 1000, Height = 1000 },
                    Editor = new() { X = 0, Y = 300, Width = 1000, Height = 600 } };
                var x = (int)(34 * scale); var y = (int)(69 * scale);
                void Paint(double opacity)
                {
                    using var g = Graphics.FromImage(image); g.Clear(Color.FromArgb(31, 31, 31));
                    using var faded = (Bitmap)hint.Clone();
                    for (var cy = 0; cy < faded.Height; cy++)
                        for (var cx = 0; cx < faded.Width; cx++)
                        {
                            var c = hint.GetPixel(cx, cy);
                            faded.SetPixel(cx, cy, Color.FromArgb((int)Math.Round(31 + (c.R - 31) * opacity),
                                (int)Math.Round(31 + (c.G - 31) * opacity), (int)Math.Round(31 + (c.B - 31) * opacity)));
                        }
                    g.DrawImage(faded, new Rectangle(x, y, hint.Width, hint.Height), new Rectangle(0, 0, hint.Width, hint.Height), GraphicsUnit.Pixel);
                }
                Paint(1); var known = VisionEditorEvidence.InspectEmpty(image, editor, dpi).Placeholder;
                if (known is null) throw new Exception("Full hint was not recognized.");
                var baseline = VisionRegionSnapshot.CaptureInput(image, layout, dpi, true, known);
                foreach (var opacity in new[] { 0d, .02, .1, .2, .4, .6, .8, 1d })
                {
                    Paint(opacity);
                    if (!baseline.Matches(VisionRegionSnapshot.CaptureInput(image, layout, dpi, true, known), true, dpi))
                        throw new Exception($"Fading UI hint invalidated input at {dpi} DPI, opacity {opacity}.");
                }
                Paint(.1);
                using (var g = Graphics.FromImage(image)) g.FillRectangle(Brushes.White, x, y, (int)(12 * scale), (int)(18 * scale));
                if (baseline.Matches(VisionRegionSnapshot.CaptureInput(image, layout, dpi, true, known), true, dpi))
                    throw new Exception("Known hint area hid a real strong change.");
                Paint(0);
                using (var g = Graphics.FromImage(image))
                { using var brush = new SolidBrush(Color.FromArgb(43, 43, 43)); g.FillRectangle(brush, image.Width - 10, image.Height - 10, 1, 1); }
                if (baseline.Matches(VisionRegionSnapshot.CaptureInput(image, layout, dpi, true, known), true, dpi))
                    throw new Exception("Faint changes outside the known UI hint were ignored.");
            }
            return Task.CompletedTask;
        });
        yield return ("Input change diagnostics retain the exact region and pixel colors for debugging", () =>
        {
            using var first = new Bitmap(100, 70); using var g = Graphics.FromImage(first); g.Clear(Color.White);
            var baseline = VisionRegionSnapshot.Capture(first, new(0, 0, 100, 70));
            g.FillRectangle(Brushes.Green, 10, 12, 20, 30);
            var changed = VisionRegionSnapshot.Capture(first, new(0, 0, 100, 70));
            var difference = baseline.Difference(changed);
            if (difference.Pixels != 600 || difference.Bounds != new Rectangle(10, 12, 20, 30)) throw new Exception("Pixel change bounds were lost.");
            using var bytes = new MemoryStream(changed.ToPng()); using var exported = new Bitmap(bytes);
            if (exported.Size != first.Size || exported.GetPixel(12, 15).ToArgb() != Color.Green.ToArgb() ||
                exported.GetPixel(80, 60).ToArgb() != Color.White.ToArgb()) throw new Exception("Diagnostic pixels changed during export.");
            return Task.CompletedTask;
        });
        yield return ("Validated empty-input monitoring ignores only a known hint appearing or disappearing", () =>
        {
            using var image = new Bitmap(240, 160); using var g = Graphics.FromImage(image);
            g.Clear(Color.FromArgb(31, 31, 31)); g.FillRectangle(Brushes.LimeGreen, 30, 65, 2, 19);
            var layout = new VisionLayout { Input = new() { X = 0, Y = 0, Width = 1000, Height = 1000 },
                Editor = new() { X = 0, Y = 300, Width = 1000, Height = 600 } };
            var noHint = VisionRegionSnapshot.CaptureInput(image, layout, 96, true);
            using var hint = WechatEmptyPlaceholder.Load(96);
            g.DrawImage(hint, new Rectangle(34, 69, hint.Width, hint.Height), new Rectangle(0, 0, hint.Width, hint.Height), GraphicsUnit.Pixel);
            using (var faint = new SolidBrush(Color.FromArgb(43, 43, 43))) g.FillRectangle(faint, 34, 68, 1, 1);
            var raw = VisionRegionSnapshot.CaptureInput(image, layout);
            var withHint = VisionRegionSnapshot.CaptureInput(image, layout, 96, true);
            if (noHint.Matches(raw, true) || !noHint.Matches(withHint, true) || !withHint.Matches(noHint, true))
                throw new Exception("Known UI decoration still invalidates a validated empty draft.");
            g.FillRectangle(Brushes.White, 34, 68, 1, 1);
            if (noHint.Matches(VisionRegionSnapshot.CaptureInput(image, layout, 96, true), true))
                throw new Exception("A strong change outside the matched hint was normalized away.");
            g.Clear(Color.FromArgb(31, 31, 31)); g.FillRectangle(Brushes.LimeGreen, 30, 65, 2, 19);
            using var font = new Font("Microsoft YaHei UI", 14); g.DrawString("这是新输入的文字", font, Brushes.White, 34, 65);
            if (noHint.Matches(VisionRegionSnapshot.CaptureInput(image, layout, 96, true), true))
                throw new Exception("Real draft text was normalized away.");
            return Task.CompletedTask;
        });
        yield return ("OCR local title is authoritative in the HTTP protocol and model title is optional", async () =>
        {
            string body = "";
            var response = JsonSerializer.Serialize(new ModelResponse { GreetingText = "生日快乐！愿你新的一岁平安顺心，每个平常的日子里都有值得期待的小事。" });
            var handler = new StubHandler(request => { body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = response } } } })) }; });
            using var client = new CompatibleModelClient(handler);
            await client.GenerateAsync(configuration, "", [new("c", [1], true, "测试联系人")], null, [], true, default);
            using var json = JsonDocument.Parse(body);
            var messages = json.RootElement.GetProperty("messages");
            var text = messages[1].GetProperty("content").EnumerateArray().Last().GetProperty("text").GetString()!;
            if (!text.Contains("不重新读取标题") || handler.Calls != 1) throw new Exception("Model still determined the OCR title.");
            try { DraftRules.ValidateConversation(new() { ObservedDisplayName = "普通人" }, [new("c", [1], true, "测试群(242)")]); }
            catch (GroupChatDetectedException) { return; }
            throw new Exception("Local group title was replaced by a model title.");
        });
        yield return ("OCR title cannot become an unsupported salutation when no model title is returned", () =>
        {
            try { DraftRules.ValidateSalutation(new() { GreetingText = "测试联系人，生日快乐！" }, null, [new("c", [1])], "测试联系人"); }
            catch (InvalidDataException) { return Task.CompletedTask; }
            throw new Exception("OCR title was silently used as a salutation.");
        });
    }
}
