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
        yield return ("Local crops exclude the contact list and entire input panel", () =>
        {
            using var image = Fixture(192, false);
            var result = WechatLocalLayout.Locate(image, 192, default);
            var frame = new VisionWindowFrame(new(100, -200, image.Width, image.Height), 192);
            var messages = frame.ToClientRectangle(result.Layout.Messages!);
            var input = frame.ToClientRectangle(result.Layout.Input!);
            using var graphics = Graphics.FromImage(image);
            graphics.FillRectangle(Brushes.Magenta, 0, 0, messages.Left - 2, image.Height);
            graphics.FillRectangle(Brushes.Magenta, input);
            graphics.Flush();
            var capture = VisionContextSnapshot.CreateCapture(image, frame, result.Layout, 0);
            using var stream = new MemoryStream(capture.PngBytes); using var crop = new Bitmap(stream);
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

    private static Bitmap Fixture(uint dpi, bool toolbarAtTop)
    {
        var image = TemplateLocatorChecks.Fixture(dpi, false);
        using var graphics = Graphics.FromImage(image); using var pen = new Pen(Color.Gray);
        var separator = toolbarAtTop ? 610 - (int)(20 * dpi / 96d) : 450;
        graphics.DrawLine(pen, 300, separator, image.Width - 5, separator);
        return image;
    }
    private static void MustReject(Action check)
    { try { check(); } catch (InvalidDataException) { return; } throw new Exception("Expected geometry/semantic rejection."); }
}
