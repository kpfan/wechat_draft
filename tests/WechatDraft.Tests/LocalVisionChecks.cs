using System.Drawing;
using System.Net;
using System.Text.Json;
using WechatDraft.Core;
using WechatDraft.Windows;

internal static class LocalVisionChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root, ExampleService examples, ISecretProtector protector)
    {
        yield return ("Title OCR reuses only identical pixels and rechecks any pixel or size change", async () =>
        {
            var engine = new CountingTitleOcr();
            var cached = new ExactTitleOcrCache(engine);
            using var title = new Bitmap(120, 40);
            using (var graphics = Graphics.FromImage(title)) graphics.Clear(Color.White);
            if (await cached.ReadAsync(title, default) != "title-1" || await cached.ReadAsync(title, default) != "title-1")
                throw new Exception("Identical input was not reused.");
            title.SetPixel(60, 20, Color.Black);
            if (await cached.ReadAsync(title, default) != "title-2") throw new Exception("A one-pixel title change reused old OCR.");
            using var resized = new Bitmap(121, 40);
            if (await cached.ReadAsync(resized, default) != "title-3") throw new Exception("Changed crop dimensions reused old OCR.");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await cached.ReadAsync(resized, cancellation.Token); throw new Exception("Cancelled OCR cache access succeeded."); }
            catch (OperationCanceledException) { }
            var anotherTask = new ExactTitleOcrCache(engine);
            if (await anotherTask.ReadAsync(resized, default) != "title-4") throw new Exception("OCR cache escaped the task lifetime.");
        });
        yield return ("Greeting protocol requests the full title but not model conversation classification", async () =>
        {
            foreach (var group in new[] { false, true })
            {
                var body = "";
                var response = JsonSerializer.Serialize(new ModelResponse { ObservedDisplayName = group ? "测试群（242）" : "测试好友",
                    ConversationType = group ? "single" : "service", GreetingText = "生日快乐！愿你新的一岁平安顺心，每个平常的日子里都有值得期待的小事。" });
                var handler = new StubHandler(request =>
                {
                    body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                    { choices = new[] { new { message = new { content = response } } } })) };
                });
                using var client = new CompatibleModelClient(handler);
                var configuration = new AppSettings { Model = "fake", Scene = "生日", BaseUrl = "https://test.example/v1" };
                var rejected = false;
                try { await client.GenerateAsync(configuration, "", [new("c", [1], RequiresConversationCheck: true)], null, [], true, default); }
                catch (GroupChatDetectedException) { rejected = true; }
                if (rejected != group || handler.Calls != 1 || body.Contains("conversation_type", StringComparison.Ordinal))
                    throw new Exception("Model classification was still requested or trusted.");
                using var json = JsonDocument.Parse(body);
                var system = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                if (!system.Contains("必须保留末尾的括号人数") || !system.Contains("推广文案"))
                    throw new Exception("The model was not instructed to retain count suffixes and avoid type guesses.");
            }
        });
        yield return ("Local read-only generation prepares a frozen context without requiring title OCR", async () =>
        {
            using var image = Fixture(192, false); // No title text to OCR at all.
            using var adapter = new WechatVisionAdapter(0);
            await adapter.PrepareReadonlyFrameAsync(image, 192, default);
            var snapshot = adapter.CreateReadonlySnapshot(default);
            var capture = await adapter.CaptureAsync(snapshot, 0, default);
            if (snapshot.DisplayNameRead || snapshot.OriginalTextRead || snapshot.DisplayName.Length != 0 ||
                !capture.RequiresConversationCheck || capture.PngBytes.Length == 0 || adapter.LayoutApiCallCount != 0)
                throw new Exception("Read-only visual generation depended on OCR or lost provenance.");
        });
        yield return ("Model-read visual titles containing emoji remain display metadata without contact mutation or writes", async () =>
        {
            var repo = new LocalRepository(Path.Combine(root, "emoji-" + Guid.NewGuid().ToString("N")), protector);
            const string title = "测试好友🎀-研发部";
            repo.SaveContact(new Contact { DisplayName = title, RemarkName = title, Confirmed = true, AddressTerm = "陈老师", AddressTermSource = "user" });
            var configuration = new AppSettings { DemoMode = false, Scene = "生日", BaseUrl = "https://test.example/v1",
                AuthorizedOrigin = "https://test.example", Model = "fake", EnableLiveAppend = true };
            var adapter = new FakeAdapter { IsDemo = false, CanAutoAppend = false, OriginalTextRead = false,
                DisplayNameRead = false, DisplayName = "", CanCollectMoreContext = false };
            var model = new FakeModel { Response = new() { ObservedDisplayName = title, ConversationType = "single",
                GreetingText = "生日快乐！愿你新的一岁平安顺心，每个平常的日子里都有值得期待的小事。" } };
            var result = await new TaskCoordinator(repo, examples, protector).RunAsync(configuration, adapter, model, null, default);
            if (result!.GenerationStatus != "成功" || result.DisplayName != title || result.ScreenshotRecipientMatched is not null ||
                result.ContactId is not null || result.ContactReused || model.LastContact is not null || adapter.Appends != 0 || result.AppendAttemptId is not null)
                throw new Exception("A model-read title was blocked by OCR or treated as independent identity.");
            if (repo.Contacts("default").Count != 1 || repo.Contacts("default")[0].AddressTerm != "陈老师")
                throw new Exception("Read-only visual analysis mutated a saved contact.");
            adapter = new FakeAdapter { IsDemo = false, CanAutoAppend = false, OriginalTextRead = false,
                DisplayNameRead = false, DisplayName = "", CanCollectMoreContext = false };
            model.Response = model.Response with { ConversationType = "unknown" };
            result = await new TaskCoordinator(repo, examples, protector).RunAsync(configuration, adapter, model, null, default);
            if (result!.GenerationStatus != "成功" || model.Calls != 2 || adapter.Appends != 0 || repo.Contacts("default").Count != 1)
                throw new Exception("A model type blocked a title without a group suffix, or granted write access.");
        });
        yield return ("An adapter cannot authorize input from a snapshot without an independently read title", async () =>
        {
            var repo = new LocalRepository(Path.Combine(root, "title-" + Guid.NewGuid().ToString("N")), protector);
            foreach (var originalRead in new[] { false, true })
            {
                var adapter = new FakeAdapter { CanAutoAppend = true, DisplayNameRead = false, OriginalTextRead = originalRead };
                var model = new FakeModel();
                var result = await new TaskCoordinator(repo, examples, protector).RunAsync(new AppSettings { DemoMode = true, Scene = "生日" }, adapter, model, null, default);
                if (result!.GenerationStatus != "失败" || model.Calls != 0 || adapter.Appends != 0)
                    throw new Exception("A missing independent title granted write capability.");
            }
        });
        yield return ("Local OCR reconstructs CJK spacing without deleting real spaces in titles", () =>
        {
            var joined = WechatLocalTitleReader.JoinTitleWords([("陈", new(0, 0, 24, 30)), ("老师", new(27, 0, 48, 30))]);
            var spaced = WechatLocalTitleReader.JoinTitleWords([("陈", new(0, 0, 24, 30)), ("老师", new(36, 0, 48, 30))]);
            var english = WechatLocalTitleReader.JoinTitleWords([("John", new(0, 0, 50, 26)), ("Smith", new(59, 0, 62, 26))]);
            if (joined != "陈老师" || spaced != "陈 老师" || english != "John Smith")
                throw new Exception("OCR spaces changed the visible title.");
            return Task.CompletedTask;
        });
        yield return ("Local visual generation uses title suffixes instead of model type and never grants writes", async () =>
        {
            foreach (var type in new[] { "single", "unknown", "group", "service" })
            {
                var repo = new LocalRepository(Path.Combine(root, "local-" + Guid.NewGuid().ToString("N")), protector);
                var configuration = new AppSettings { DemoMode = false, Scene = "生日", BaseUrl = "https://test.example/v1",
                    AuthorizedOrigin = "https://test.example", Model = "fake", EnableLiveAppend = true };
                var adapter = new FakeAdapter { IsDemo = false, CanAutoAppend = false, OriginalTextRead = false,
                    DisplayNameRead = false, DisplayName = "", CanCollectMoreContext = false, RequiresConversationCheck = true };
                var model = new FakeModel { Response = new() { ObservedDisplayName = "测试联系人", ConversationType = type,
                    GreetingText = "生日快乐！愿你新的一岁平安顺心，每个平常的日子里都有值得期待的小事。" } };
                var result = await new TaskCoordinator(repo, examples, protector).RunAsync(configuration, adapter, model, null, default);
                if (result!.GenerationStatus != "成功" || model.Calls != 1 || adapter.Appends != 0 ||
                    adapter.Scrolls != 0 || result.AppendAttemptId is not null || result.LayoutApiCallCount != 0 || result.ScreenshotRecipientMatched is not null)
                    throw new Exception("Local visual semantics or read-only permissions were bypassed.");
                if (repo.Contacts(configuration.AccountScope).Count != 0)
                    throw new Exception("An unconfirmed conversation created a contact.");
            }
        });
        yield return ("Local visual fallback locates both toolbar arrangements across DPI without a model client", async () =>
        {
            foreach (var dpi in new uint[] { 96, 144, 192 })
            foreach (var toolbarAtTop in new[] { false, true })
            {
                using var image = Fixture(dpi, toolbarAtTop);
                using var adapter = new WechatVisionAdapter(0);
                var layout = await adapter.LocateFrozenAsync(image, dpi, default);
                if (adapter.LayoutApiCallCount != 0 || adapter.CanAutoAppend || adapter.CanCollectMoreContext || adapter.PreviewPng is null)
                    throw new Exception("Local geometry must be previewable and read-only with zero layout requests.");
                if (layout.Messages!.Bottom >= layout.Input!.Y || !layout.Input.Contains(layout.Editor!) ||
                    !layout.Input.Contains(layout.Send!) || !layout.Input.Contains(layout.InputToolbar!))
                    throw new Exception("Local regions overlap or leave the input panel.");
                if (toolbarAtTop ? layout.Editor!.Y <= layout.InputToolbar!.Bottom : layout.Editor!.Bottom >= layout.InputToolbar!.Y)
                    throw new Exception("The editor was placed on the wrong side of the toolbar.");
                if (layout.ConversationType != "unknown" || layout.DraftKind != "unknown" || !layout.Uncertain)
                    throw new Exception("Geometry is not semantic proof.");
                MustReject(() => VisionRules.Validate(layout));
                var result = await adapter.AppendAsync(new("s", "n", "draft", "w", false), "祝福", default);
                if (result.Status != "待手动处理") throw new Exception("A visual result authorized a write.");
            }
        });
        yield return ("Local visual layout remembers a confirmed theme per window and recovers after it changes", () =>
        {
            var window = Guid.NewGuid().ToString("N");
            var reads = 0;
            WechatToolbarTheme ReadTheme() { reads++; return WechatToolbarTheme.Dark; }
            using var light = TemplateLocatorChecks.Fixture(192, false, WechatToolbarTheme.Light);
            MustReject(() => WechatLocalLayout.Locate(light, 192, default, window, ReadTheme));
            using (var graphics = Graphics.FromImage(light))
                graphics.DrawLine(Pens.Gray, 300, 450, light.Width - 5, 450);
            _ = WechatLocalLayout.Locate(light, 192, default, window, ReadTheme);
            _ = WechatLocalLayout.Locate(light, 192, default, window, ReadTheme);
            using var dark = Fixture(192, false);
            _ = WechatLocalLayout.Locate(dark, 192, default, window, ReadTheme);
            _ = WechatLocalLayout.Locate(dark, 192, default, Guid.NewGuid().ToString("N"), ReadTheme);
            if (reads != 3) throw new Exception("Theme hints must be read for new windows and cached only after valid geometry.");
            return Task.CompletedTask;
        });
        yield return ("Local layout follows a left-shifted toolbar and a tall editor across window widths", () =>
        {
            const uint dpi = 144;
            const int toolbarX = 112, chatLeft = 88, separator = 215;
            foreach (var width in new[] { 636, 838 })
            foreach (var theme in new[] { WechatToolbarTheme.Dark, WechatToolbarTheme.Light })
            {
                using var image = TemplateLocatorChecks.Fixture(dpi, false, theme, width, width - 216, toolbarX);
                using (var graphics = Graphics.FromImage(image))
                    graphics.DrawLine(Pens.Gray, chatLeft, separator, width - 12, separator);

                var (layout, anchors) = WechatLocalLayout.Locate(image, dpi, default, readWindowsTheme: () => theme);
                if (Math.Abs(anchors.BottomToolbar.X * width / 1000 - toolbarX) > 1 ||
                    Math.Abs(layout.Chat!.X * width / 1000 - chatLeft) > 1 ||
                    Math.Abs(layout.Input!.Y * image.Height / 1000 - separator) > 1 ||
                    !layout.Input.Contains(layout.Editor!) || !layout.Input.Contains(layout.InputToolbar!))
                    throw new Exception($"The left-shifted layout was incorrect at {width} px in {theme} theme.");
            }
            return Task.CompletedTask;
        });
        yield return ("Local layout finds a tall editor boundary above two fifths of the window", () =>
        {
            const int separator = 310;
            using var image = Fixture(192, false, separator);
            var (layout, _) = WechatLocalLayout.Locate(image, 192, default);
            if (Math.Abs(layout.Input!.Y * image.Height / 1000 - separator) > 1 ||
                layout.Messages!.Bottom >= layout.Input.Y || !layout.Input.Contains(layout.Editor!))
                throw new Exception("The input boundary or its derived regions were incorrect.");
            return Task.CompletedTask;
        });
        yield return ("An image against the input divider does not prevent layout and write-time geometry checks", () =>
        {
            const int separator = 450;
            using var initial = Fixture(192, false, separator);
            using var withImage = (Bitmap)initial.Clone();
            using (var graphics = Graphics.FromImage(withImage))
                graphics.FillRectangle(Brushes.White, 435, 380, 155, separator - 380);

            var (layout, anchors) = WechatLocalLayout.Locate(initial, 192, default);
            var (recovered, _) = WechatLocalLayout.Locate(withImage, 192, default);
            if (recovered.Input!.Y != layout.Input!.Y ||
                !WechatLocalLayout.CanReuseGeometry(initial, withImage, 192, layout, anchors, default))
                throw new Exception("A message image moved or hid the divider during layout checks.");
            return Task.CompletedTask;
        });
        yield return ("A short message edge cannot stand in for the input divider", () =>
        {
            using var image = TemplateLocatorChecks.Fixture(192, false);
            using (var graphics = Graphics.FromImage(image))
                graphics.DrawLine(Pens.Gray, 430, 450, 980, 450);
            MustReject(() => WechatLocalLayout.Locate(image, 192, default));
            return Task.CompletedTask;
        });
        yield return ("Both editor corners recover the divider when a wide message hides the upper reference", () =>
        {
            const int y = 450, left = 332, right = 1072;
            using var lineOnly = TemplateLocatorChecks.Fixture(192, false);
            using (var graphics = Graphics.FromImage(lineOnly))
            {
                graphics.FillRectangle(Brushes.White, 410, 380, 550, y - 380);
                graphics.DrawLine(Pens.Gray, left + 16, y, right - 16, y);
            }
            MustReject(() => WechatLocalLayout.Locate(lineOnly, 192, default));

            using var framed = (Bitmap)lineOnly.Clone();
            using (var graphics = Graphics.FromImage(framed))
            {
                foreach (var (edge, inward) in new[] { (left, 1), (right, -1) })
                {
                    graphics.DrawLines(Pens.Gray, new Point[] { new(edge + inward * 16, y),
                        new(edge + inward * 3, y + 6), new(edge, y + 12), new(edge, y + 70) });
                }
            }
            var (layout, _) = WechatLocalLayout.Locate(framed, 192, default);
            if (Math.Abs(layout.Input!.Y * framed.Height / 1000 - y) > 1)
                throw new Exception("The recovered divider does not align with the top of the editor.");
            return Task.CompletedTask;
        });
        yield return ("Antialiased editor corners recover the divider when a message touches its upper edge", () =>
        {
            const uint dpi = 144;
            const int y = 450, left = 326, right = 1088;
            var topTone = Color.FromArgb(49, 49, 49);
            var sideTone = Color.FromArgb(53, 53, 53);
            using var image = TemplateLocatorChecks.Fixture(dpi, false);
            using (var graphics = Graphics.FromImage(image))
            using (var pen = new Pen(topTone))
            using (var sidePen = new Pen(sideTone))
            {
                graphics.FillRectangle(Brushes.White, 400, 380, 540, y - 380);
                graphics.DrawLine(pen, left + 20, y, right - 20, y);
                graphics.DrawLine(sidePen, left + 12, y + 12, left + 12, y + 40);
                graphics.DrawLine(sidePen, right - 8, y + 12, right - 8, y + 40);
            }
            image.SetPixel(left + 16, y + 4, sideTone);
            image.SetPixel(right - 8, y + 4, topTone);
            MustReject(() => WechatLocalLayout.Locate(image, dpi, default));

            image.SetPixel(right - 9, y + 4, sideTone);
            var (layout, _) = WechatLocalLayout.Locate(image, dpi, default);
            if (Math.Abs(layout.Input!.Y * image.Height / 1000 - y) > 1)
                throw new Exception("The antialiased editor corner moved the divider.");
            return Task.CompletedTask;
        });
        yield return ("A two-pixel corner at 100 percent scaling recovers an obscured input divider", () =>
        {
            const int y = 450, left = 343, right = 1083;
            using var image = TemplateLocatorChecks.Fixture(96, false);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.FillRectangle(Brushes.White, 410, 400, 550, y - 400);
                graphics.DrawLine(Pens.Gray, left + 8, y, right - 8, y);
                foreach (var (edge, inward) in new[] { (left, 1), (right, -1) })
                {
                    var outer = Math.Min(edge, edge + inward);
                    graphics.FillRectangle(Brushes.Gray, outer, y + 6, 2, 40);
                    graphics.FillRectangle(Brushes.Gray, outer + inward * 3, y + 3, 2, 1);
                }
            }
            var (layout, _) = WechatLocalLayout.Locate(image, 96, default);
            if (Math.Abs(layout.Input!.Y * image.Height / 1000 - y) > 1)
                throw new Exception("The recovered boundary is outside the input divider.");
            return Task.CompletedTask;
        });
        yield return ("Multiple separated partial divider candidates are rejected as ambiguous", () =>
        {
            using var image = TemplateLocatorChecks.Fixture(192, false);
            using (var graphics = Graphics.FromImage(image))
            {
                foreach (var y in new[] { 430, 510 })
                {
                    graphics.FillRectangle(Brushes.White, 435, y - 30, 155, 30);
                    graphics.DrawLine(Pens.Gray, 300, y, image.Width - 5, y);
                }
            }
            MustReject(() => WechatLocalLayout.Locate(image, 192, default));
            return Task.CompletedTask;
        });
        yield return ("Visual scrolling rematches changed toolbar pixels without accepting a moved input boundary", () =>
        {
            using var initial = Fixture(192, false);
            var (layout, anchors) = WechatLocalLayout.Locate(initial, 192, default);
            var frame = new VisionWindowFrame(new(0, 0, initial.Width, initial.Height), 192);
            bool CanReuse(Bitmap current) => WechatLocalLayout.CanReuseGeometry(initial, current, 192, layout, anchors, default);
            using var newMessage = (Bitmap)initial.Clone();
            var messages = frame.ToClientRectangle(layout.Messages!);
            newMessage.SetPixel(messages.X + messages.Width / 2, messages.Y + 10, Color.Magenta);
            if (!CanReuse(newMessage)) throw new Exception("A message pixel changed the confirmed toolbar geometry.");
            foreach (var box in new[] { anchors.TopToolbar, anchors.BottomToolbar })
            {
                using var changed = (Bitmap)initial.Clone();
                var region = frame.ToClientRectangle(box);
                changed.SetPixel(region.X + region.Width / 2, region.Y + region.Height / 2, Color.Magenta);
                if (CanReuse(changed)) throw new Exception("A changed toolbar reused the original geometry.");
                if (!WechatLocalLayout.ConfirmMessageGeometry(initial, changed, frame, layout, anchors, default))
                    throw new Exception("The message crop was rejected after local rematching confirmed the same geometry.");
            }
            using var movedSeparator = Fixture(192, false, 420);
            using var noSeparator = TemplateLocatorChecks.Fixture(192, false);
            if (CanReuse(movedSeparator) || CanReuse(noSeparator))
                throw new Exception("The old input bounds were used without confirming the current divider.");
            if (WechatLocalLayout.ConfirmMessageGeometry(initial, movedSeparator, frame, layout, anchors, default))
                throw new Exception("Local rematching accepted an old crop after the input divider moved.");
            return Task.CompletedTask;
        });
        yield return ("Missing or ambiguous local anchors stop without a remote layout request", async () =>
        {
            using var blank = new Bitmap(1100, 800);
            using var duplicate = TemplateLocatorChecks.Fixture(192, true);
            foreach (var image in new[] { blank, duplicate })
            {
                using var adapter = new WechatVisionAdapter(0);
                try { await adapter.LocateFrozenAsync(image, 192, default); throw new Exception("Unreliable anchors were accepted."); }
                catch (InvalidDataException) { }
                if (adapter.LayoutApiCallCount != 0 || adapter.PreviewPng is not null) throw new Exception("Failed geometry created a remote call or preview.");
            }
        });
        yield return ("Local geometry rejects a missing input boundary and preserves cancellation", async () =>
        {
            using var image = TemplateLocatorChecks.Fixture(192, false);
            using var adapter = new WechatVisionAdapter(0);
            try { await adapter.LocateFrozenAsync(image, 192, default); throw new Exception("A missing divider was guessed."); }
            catch (InvalidDataException) { }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await adapter.LocateFrozenAsync(image, 192, cancellation.Token); throw new Exception("A cancelled search continued."); }
            catch (OperationCanceledException) { }
        });
        yield return ("Local generation uses the original message crop without title, input or padding", () =>
        {
            using var image = Fixture(192, false);
            var result = WechatLocalLayout.Locate(image, 192, default);
            var frame = new VisionWindowFrame(new(100, -200, image.Width, image.Height), 192);
            var messages = frame.ToClientRectangle(result.Layout.Messages!);
            var input = frame.ToClientRectangle(result.Layout.Input!);
            using var graphics = Graphics.FromImage(image);
            graphics.FillRectangle(Brushes.Magenta, 0, 0, messages.Left - 2, image.Height);
            graphics.FillRectangle(Brushes.Magenta, input);
            graphics.FillRectangle(Brushes.Magenta, frame.ToClientRectangle(result.Layout.Header!));
            graphics.FillRectangle(Brushes.Lime, messages.Left, messages.Top, 6, 6);
            graphics.Flush();
            var capture = VisionContextSnapshot.CreateCapture(image, frame, result.Layout, 0);
            using var stream = new MemoryStream(capture.PngBytes); using var crop = new Bitmap(stream);
            if (crop.Size != messages.Size || crop.GetPixel(0, 0).ToArgb() != Color.Lime.ToArgb())
                throw new Exception("The message crop was scaled, shifted or padded.");
            for (var y = 0; y < crop.Height; y += 3)
                for (var x = 0; x < crop.Width; x += 3)
                    if (crop.GetPixel(x, y).ToArgb() == Color.Magenta.ToArgb()) throw new Exception("Private neighboring pixels entered the model crop.");
            return Task.CompletedTask;
        });
        yield return ("Local visual generation validates full title and ignores obsolete model classification", () =>
        {
            Capture[] captures = [new("local", [1], RequiresConversationCheck: true)];
            foreach (var type in new[] { "single", "unknown", "group", "service", "" })
                DraftRules.ValidateConversation(new() { ObservedDisplayName = "测试联系人", ConversationType = type }, captures);
            foreach (var title in new[] { null, "", "   ", "标题\n文字", new string('长', 81) })
                MustReject(() => DraftRules.ValidateConversation(new() { ObservedDisplayName = title }, captures));
            DraftRules.ValidateConversation(new(), [new("uia", [1])]);
            return Task.CompletedTask;
        });
        yield return ("Only a paired trailing parenthesized count greater than two matches the group-title rule", () =>
        {
            foreach (var title in new[] { "测试群(3)", "测试群（242）", "【交流课】开发者大会-2(242)", "测试群 ( 003 )  ", "(000000003)", "测试群(999999999999999999999999999999)" })
                if (!DraftRules.LooksLikeGroupTitle(title)) throw new Exception("Missed group suffix: " + title);
            foreach (var title in new[] { null, "", "测试好友", "测试好友(0)", "测试好友（1）", "测试好友(2)", "测试好友(0002)",
                "测试好友(242)-备注", "测试好友(2.5)", "测试好友(-3)", "测试好友(三)", "测试好友(3）", "测试好友（3)", "测试好友[3]" })
                if (DraftRules.LooksLikeGroupTitle(title)) throw new Exception("False group suffix: " + title);
            return Task.CompletedTask;
        });
        yield return ("OCR spaces and fullwidth digits in trailing member counts cannot bypass group rejection", () =>
        {
            foreach (var title in new[] { "测试群 （1 12）", "测试群(1 1 2)", "测试群（１ １２）", "测试群(００ ３)",
                "测试群（1\u00a012）", "测试群（1\u202f12）", "测试群（1\u300012）", "测试群（９９ ９９９９９９９９９９９９９９９９９９９９９９）" })
            {
                if (!DraftRules.LooksLikeGroupTitle(title)) throw new Exception("Split OCR count was accepted: " + title);
                try { OcrPrefillGuard.ValidateTitle(title); }
                catch (GroupChatDetectedException) { continue; }
                throw new Exception("OCR title retained prefill permission.");
            }
            foreach (var title in new[] { "测试好友（０ ０ ２）", "测试好友（0 1）", "测试好友（1 12）备注", "测试好友（1.12）", "测试好友（1 A2）" })
                if (DraftRules.LooksLikeGroupTitle(title)) throw new Exception("Non-count title was changed into a group.");
            return Task.CompletedTask;
        });
        yield return ("Independent group titles stop before model calls, contact updates or append intents", async () =>
        {
            foreach (var source in new[] { "ocr", "uia" })
            {
                var repository = new LocalRepository(Path.Combine(root, "group-preflight-" + Guid.NewGuid().ToString("N")), protector);
                var adapter = new FakeAdapter { IsDemo = false, DisplayName = "测试群 （1 12）", TitleSource = source };
                var model = new FakeModel();
                var configuration = new AppSettings { DemoMode = false, Model = "fake", Scene = "生日", EnableLiveAppend = true,
                    BaseUrl = "https://test.example/v1", AuthorizedOrigin = "https://test.example" };
                var result = await new TaskCoordinator(repository, examples, protector).RunAsync(configuration, adapter, model, null, default);
                if (model.Calls != 0 || result!.ApiCallCount != 0 || result.GenerationStatus != "失败" || result.Greeting.Length != 0 ||
                    adapter.Appends != 0 || result.AppendAttemptId is not null || repository.Contacts("default").Count != 0 || !result.Error.Contains("群聊"))
                    throw new Exception("Group title reached generation or input before validation.");
            }
        });
        yield return ("Detected group-title counts stop once without retrying or saving a contact", async () =>
        {
            var repo = new LocalRepository(Path.Combine(root, "suffix-" + Guid.NewGuid().ToString("N")), protector);
            var configuration = new AppSettings { DemoMode = false, Scene = "生日", BaseUrl = "https://test.example/v1",
                AuthorizedOrigin = "https://test.example", Model = "fake", EnableLiveAppend = true };
            var adapter = new FakeAdapter { IsDemo = false, CanAutoAppend = false, OriginalTextRead = false,
                DisplayNameRead = false, DisplayName = "", CanCollectMoreContext = false };
            var model = new FakeModel { Response = new() { ObservedDisplayName = "测试群(242)", ConversationType = "single",
                GreetingText = "生日快乐！愿你新的一岁平安顺心，每个平常的日子里都有值得期待的小事。" } };
            var result = await new TaskCoordinator(repo, examples, protector).RunAsync(configuration, adapter, model, null, default);
            if (result!.GenerationStatus != "失败" || !result.Error.Contains("括号人数大于 2") || model.Calls != 1 ||
                adapter.Appends != 0 || result.AppendAttemptId is not null || repo.Contacts("default").Count != 0)
                throw new Exception("Group suffix retried, created a contact, or granted write access.");
        });
    }

    private sealed class CountingTitleOcr : IChatTitleOcr
    {
        private int _calls;
        public string Name => "fake";
        public Task<string> ReadAsync(Bitmap titleImage, CancellationToken cancellationToken) => Task.FromResult("title-" + ++_calls);
    }

    private static Bitmap Fixture(uint dpi, bool toolbarAtTop, int? separatorY = null)
    {
        var image = TemplateLocatorChecks.Fixture(dpi, false);
        using var graphics = Graphics.FromImage(image); using var pen = new Pen(Color.Gray);
        var separator = separatorY ?? (toolbarAtTop ? 610 - (int)(20 * dpi / 96d) : 450);
        graphics.DrawLine(pen, 300, separator, image.Width - 5, separator);
        return image;
    }
    private static void MustReject(Action check)
    { try { check(); } catch (InvalidDataException) { return; } throw new Exception("Expected geometry/semantic rejection."); }
}
