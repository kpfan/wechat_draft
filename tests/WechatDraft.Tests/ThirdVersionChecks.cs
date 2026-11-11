using System.Net;
using System.Text;
using System.Text.Json;
using WechatDraft.Core;
using WechatDraft.Core.ThirdVersion;

internal static class ThirdVersionChecks
{
    // Independent complete texts keep the test focused on behavior rather than near-duplicate rewrites.
    private static readonly string[] Texts =
    [
        "新春快乐！愿新的一年里，忙碌的工作有清晰的方向，付出的心力都有踏实的回响，生活中的小小期待也能逐一实现。无论日程怎样安排，都能给自己留出休息的空当，吃得安心、睡得安稳，与家人共享平常日子的温暖，收获健康、顺遂和长久的好心情。[庆祝][福]",
        "春节给您拜年了！借着这份节日问候，送上对未来生活的美好祝愿：愿脚下的道路越走越宽，手中的事情稳稳当当，偶尔遇到难题时也有从容应对的力量。愿三餐四季都有舒心滋味，亲友相伴时笑声常在，独处之际自在安宁，在新的春天迎来更多值得珍惜的欢喜。[烟花][爆竹]",
        "过年好！在这辞旧迎新的时刻，祝您身体康健、阖家和乐，把烦恼留在身后，让希望慢慢生长。愿今后的每次出发都有好风景，每段努力都能收获新的体会，既有向前奔跑的热情，也有停下欣赏生活的闲心，日子不疾不徐，心里常有明亮与笃定。[福][庆祝]",
        "新春的问候送到了！愿您把这一年的日子过成自己喜欢的模样，有认真投入的事情，也有自由安排的时光，不被琐碎消磨兴致，不因暂时的曲折丢掉信心。祝愿新的旅程处处有收获，案头事务井然有序，家中灯火温暖可亲，身边的人都安好，平凡生活也能积攒充足的幸福。[烟花][福]",
        "春节到了，向您送上一份真诚的祝福！愿新岁打开崭新的篇章，计划中的事情顺利推进，尚未决定的方向慢慢清晰，每一份坚持都有意义。愿您在忙与闲之间找到合适的节奏，保有充沛精力和轻松心情，也能享受一杯热茶、一顿好饭带来的满足，让平安与喜悦长久相随。[庆祝][爆竹]",
        "在这个团圆的节日里，祝您新春愉快！愿窗外的春意带来新的希望，心中的愿望获得耐心照料，漫长的时光里总有让人安心的小事。愿您做事得心应手，遇事从容淡定，与亲友相聚时自在畅谈，在寻常的晨昏之间感受生活的美好，把每一天都过得充实、健康而有盼头。[福][烟花]",
        "新年好，愿您一路平安！岁序更新的时候，最想送上的仍是朴实的惦念：冷暖适宜，饮食可口，精神饱满，心中安宁。愿您遇见愿意倾听的朋友，读到带来启发的好书，在自己珍惜的领域不断发现乐趣，即使偶有风雨，也能妥帖照顾自己，把日子经营得踏实丰盛，让所有美好慢慢抵达。[庆祝][福]"
    ];

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("Third version unknown extraction pauses until the user resolves an address", async () =>
        {
            using var fixture = new Fixture(root, (_, _, _) => Task.FromResult(Reply(Unknown())));
            var session = await fixture.ExtractAsync();
            Equal("unknown", session.Extraction.Basis); Equal<string?>(null, session.Extraction.Name);
            Equal(0, session.Candidates.Count); Equal(0, session.Revisions.Count); Equal(1, session.ApiCallCount);
            await RejectAsync(() => fixture.Service.GenerateAsync(session, null, false, default));
            Equal(1, fixture.Handler.Calls); Equal(1, session.ApiCallCount);
        });
        yield return ("Third version manual and neutral resolution each require exactly one generation request", async () =>
        {
            foreach (var neutral in new[] { false, true })
            {
                var address = neutral ? null : "周老师";
                using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(Reply(call == 1 ? Unknown() : Candidates(0, address))));
                var session = await fixture.ExtractAsync();
                await fixture.Service.GenerateAsync(session, address, neutral, default);
                Equal(3, session.Candidates.Count); Equal(3, session.Revisions.Count); Equal(2, session.ApiCallCount);
                Equal(neutral, session.Neutral); Equal(address, session.ResolvedAddress);
                for (var i = 0; i < 3; i++)
                {
                    Equal(Greeting(i, address), session.Candidates[i]); Equal(session.Candidates[i], session.Revisions[i].Greeting);
                    Equal(0, session.Revisions[i].Turns.Count);
                }
            }
        });
        yield return ("Third version rejects any count other than three and caps quality repair at one retry", async () =>
        {
            foreach (var count in new[] { 0, 1, 2, 4 })
            {
                using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(Reply(call == 1 ? Direct() :
                    JsonSerializer.Serialize(new { candidates = Enumerable.Range(0, count).Select(i => Greeting(i % 6, "周老师")) }))));
                var session = await fixture.ExtractAsync();
                await RejectAsync(() => fixture.Service.GenerateAsync(session, null, false, default));
                Equal(3, session.ApiCallCount); Equal(3, fixture.Handler.Calls); Equal(0, session.Candidates.Count);
            }
        });
        yield return ("Third version extraction preserves direct, inferred body and title evidence as distinct sources", async () =>
        {
            // Concrete evidence payloads are deliberately separate: a title must never become a body quote.
            foreach (var basis in new[] { "direct", "inferred" })
            {
                var response = basis == "direct" ? Direct() : Inferred();
                using var fixture = new Fixture(root, (_, _, _) => Task.FromResult(Reply(response)));
                var session = await fixture.ExtractAsync();
                Equal(basis, session.Extraction.Basis);
                True(session.Extraction.EvidenceItems.Count > 0, "Extraction lost evidence provenance.");
                True(session.Extraction.EvidenceItems.All(e => e.CaptureId == "capture_0"), "Extraction lost capture identity.");
                if (basis == "inferred")
                {
                    True(session.Extraction.EvidenceItems.Any(e => e.Source == "chat_title"), "Title evidence was not retained as a title.");
                    True(session.Extraction.EvidenceItems.Any(e => e.Source == "chat_message" && e.Speaker == "other"), "Inferred body evidence lost its speaker/source.");
                }
                else True(session.Extraction.EvidenceItems.Any(e => e.Source == "chat_message" && e.Speaker == "self"), "Direct evidence lost its speaker/source.");
            }
        });
        yield return ("Third version inferred self-introduction and title fallback retain their own provenance", async () =>
        {
            foreach (var titleOnly in new[] { false, true })
            {
                var name = titleOnly ? "张老师" : "姐姐";
                var source = titleOnly ? "chat_title" : "chat_message";
                var speaker = titleOnly ? "unknown" : "other";
                var quote = titleOnly ? "张老师" : "我是你姐姐，这是我的新号";
                var response = JsonSerializer.Serialize(new { name, basis = "inferred", evidence = quote, evidence_items = new[] {
                    new { source, speaker, quote, capture_id = "capture_0" } } });
                using var fixture = new Fixture(root, (_, _, _) => Task.FromResult(Reply(response)));
                var session = await fixture.ExtractAsync();
                Equal("inferred", session.Extraction.Basis); Equal(name, session.Extraction.Name);
                Equal(1, session.Extraction.EvidenceItems.Count); Equal(source, session.Extraction.EvidenceItems[0].Source);
                Equal(speaker, session.Extraction.EvidenceItems[0].Speaker); Equal(quote, session.Extraction.EvidenceItems[0].Quote);
                Equal("capture_0", session.Extraction.EvidenceItems[0].CaptureId); Equal(1, session.ApiCallCount);
            }
        });
        yield return ("Third version uses locally supplied OCR title rather than a conflicting model identity", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(Reply(call == 1 ? Inferred("错误的模型标题") : Inferred())));
            var session = await fixture.ExtractAsync(captures: [new("capture_0", [1, 2, 3], true, "妹妹")]);
            Equal("妹妹", session.Extraction.ObservedDisplayName);
            True(fixture.Handler.Bodies[0].Contains("妹妹"), "OCR title was not supplied to extraction.");
            Equal(2, fixture.Handler.Calls); Equal(2, session.ApiCallCount);
        });
        yield return ("Third version confirmed blank contact does not acquire an inferred address", async () =>
        {
            using var fixture = new Fixture(root, (_, _, _) => Task.FromResult(Reply(Inferred())));
            var session = await fixture.ExtractAsync(contact: new Contact { Confirmed = true, DisplayName = "妹妹", AddressTerm = "" });
            Equal<string?>(null, session.Extraction.Name); Equal("unknown", session.Extraction.Basis);
            await RejectAsync(() => fixture.Service.GenerateAsync(session, null, false, default));
            Equal(1, session.ApiCallCount);
        });
        yield return ("Third version snapshots mutable input and returns defensive session copies", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(Reply(call == 1 ? Direct() : Candidates())));
            var settings = Settings(); settings.ExtraInstruction = "原始写作要求";
            var contact = new Contact { DisplayName = "本地联系人", IdentityLabel = "原始身份" };
            byte[] pixels = [1, 2, 3]; List<string> recent = ["旧的祝福只作排重数据"];
            var session = await fixture.ExtractAsync(settings, [new("capture_0", pixels, true, "本地联系人")], contact, new(recent));
            settings.ExtraInstruction = "外部篡改要求"; settings.BaseUrl = "https://wrong.example/v1";
            contact.IdentityLabel = "外部篡改身份"; pixels[0] = 255; recent[0] = "外部篡改历史";
            session.Settings.ExtraInstruction = "通过 getter 篡改"; session.Contact!.IdentityLabel = "通过 getter 篡改";
            session.Captures[0].PngBytes[1] = 255;
            Equal("原始写作要求", session.Settings.ExtraInstruction); Equal("原始身份", session.Contact!.IdentityLabel);
            Equal((byte)1, session.Captures[0].PngBytes[0]); Equal((byte)2, session.Captures[0].PngBytes[1]);
            Equal("旧的祝福只作排重数据", session.Context.RecentGreetings[0]);
            await fixture.Service.GenerateAsync(session, null, false, default);
            True(!fixture.Handler.Bodies[1].Contains("外部篡改") && !fixture.Handler.Bodies[1].Contains("通过 getter"), "Mutation entered the next request.");
            Equal(2, session.ApiCallCount);
        });
        yield return ("Third version multi-turn revisions isolate branches and reset locally to immutable originals", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(Reply(call switch
            {
                1 => Direct(), 2 => Candidates(), 3 => Revision(3, "第一分支说明"),
                4 => Revision(4, "第二分支说明"), _ => Revision(5, "继续第一分支")
            })));
            var session = await fixture.ExtractAsync(); await fixture.Service.GenerateAsync(session, null, false, default);
            var originals = session.Candidates.ToArray();
            var first = await fixture.Service.ReviseAsync(session, 0, "第一分支独有修改", default);
            Equal(Greeting(3), first.Greeting); Equal("第一分支说明", first.Note); Equal(1, first.Turns.Count);
            Equal("第一分支独有修改", first.Turns[0].Instruction); Equal(first.Greeting, first.Turns[0].Greeting); Equal(first.Note, first.Turns[0].Note);
            await fixture.Service.ReviseAsync(session, 1, "第二分支独有修改", default);
            True(!fixture.Handler.Bodies[3].Contains("第一分支独有修改"), "Another branch's conversation entered the revision request.");
            await fixture.Service.ReviseAsync(session, 0, "第一分支继续修改", default);
            True(fixture.Handler.Bodies[4].Contains("第一分支独有修改") && !fixture.Handler.Bodies[4].Contains("第二分支独有修改"), "Revision did not preserve only its own history.");
            Equal(2, session.Revisions[0].Turns.Count); Equal(1, session.Revisions[1].Turns.Count); Equal(0, session.Revisions[2].Turns.Count);
            True(originals.SequenceEqual(session.Candidates), "Revision changed original candidates.");
            fixture.Service.ResetRevision(session, 0);
            Equal(originals[0], session.Revisions[0].Greeting); Equal("", session.Revisions[0].Note); Equal(0, session.Revisions[0].Turns.Count);
            Equal(1, session.Revisions[1].Turns.Count); Equal(5, session.ApiCallCount); Equal(5, fixture.Handler.Calls);
        });
        yield return ("Third version regeneration replaces all candidates and clears revision branches without re-extraction", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(Reply(call switch
            { 1 => Direct(), 2 => Candidates(), 3 => Revision(6), _ => Candidates(3) })));
            var session = await fixture.ExtractAsync(); await fixture.Service.GenerateAsync(session, null, false, default);
            await fixture.Service.ReviseAsync(session, 2, "调整祝愿组合", default);
            var extraction = session.Extraction;
            await fixture.Service.RegenerateAsync(session, default);
            Equal(extraction, session.Extraction); Equal(4, session.ApiCallCount); Equal(3, session.Candidates.Count);
            for (var i = 0; i < 3; i++)
            { Equal(Greeting(i + 3), session.Candidates[i]); Equal(session.Candidates[i], session.Revisions[i].Greeting); Equal(0, session.Revisions[i].Turns.Count); }
        });
        yield return ("Third version reloads edited prompts and examples for the very next generation", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(Reply(call == 1 ? Direct() : Candidates(call == 2 ? 0 : 3))));
            var session = await fixture.ExtractAsync(); await fixture.Service.GenerateAsync(session, null, false, default);
            File.AppendAllText(Path.Combine(fixture.PromptDirectory, "generate.txt"), "\n下一次生成配置标记甲。", Encoding.UTF8);
            File.AppendAllText(Path.Combine(fixture.PromptDirectory, "examples.txt"), "\n下一次示例配置标记乙。", Encoding.UTF8);
            await fixture.Service.RegenerateAsync(session, default);
            True(fixture.Handler.Bodies[2].Contains("下一次生成配置标记甲") && fixture.Handler.Bodies[2].Contains("下一次示例配置标记乙"), "Service reused stale prompts/examples.");
            Equal(3, session.ApiCallCount);
        });
        yield return ("Third version invalid edited prompt fails visibly before another HTTP attempt", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(Reply(call == 1 ? Direct() : Candidates())));
            var session = await fixture.ExtractAsync(); await fixture.Service.GenerateAsync(session, null, false, default);
            var originals = session.Candidates.ToArray();
            File.WriteAllText(Path.Combine(fixture.PromptDirectory, "generate.txt"), " ", Encoding.UTF8);
            await RejectAsync(() => fixture.Service.RegenerateAsync(session, default));
            Equal(2, fixture.Handler.Calls); Equal(2, session.ApiCallCount);
            True(originals.SequenceEqual(session.Candidates), "Invalid config discarded current candidates.");
        });
        yield return ("Third version retries a transient HTTP attempt once and counts failed attempts", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(call == 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Reply(call == 1 ? Direct() : Candidates())));
            var session = await fixture.ExtractAsync(); await fixture.Service.GenerateAsync(session, null, false, default);
            Equal(3, session.ApiCallCount); Equal(3, fixture.Handler.Calls); Equal(3, session.Candidates.Count);
        });
        yield return ("Third version quality repair uses one extra request and shares its cap with transport retry", async () =>
        {
            foreach (var transientFirst in new[] { false, true })
            {
                using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(call == 2 && transientFirst
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Reply(call == 1 ? Direct() : call == 2 || transientFirst ? "{\"candidates\":[]}" : Candidates())));
                var session = await fixture.ExtractAsync();
                if (transientFirst) await RejectAsync(() => fixture.Service.GenerateAsync(session, null, false, default));
                else await fixture.Service.GenerateAsync(session, null, false, default);
                Equal(3, session.ApiCallCount); Equal(3, fixture.Handler.Calls); Equal(transientFirst ? 0 : 3, session.Candidates.Count);
            }
        });
        yield return ("Third version gives regenerate and revise independent two-attempt budgets without re-extraction", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(call is 2 or 6
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Reply(call switch { 1 => Direct(), 3 => Candidates(), 4 => "{\"greeting\":\"\",\"note\":\"\"}", 5 => Revision(6), _ => Candidates(3) })));
            var session = await fixture.ExtractAsync();
            await fixture.Service.GenerateAsync(session, null, false, default); Equal(3, session.ApiCallCount);
            await fixture.Service.ReviseAsync(session, 0, "保留称呼调整语气", default); Equal(5, session.ApiCallCount);
            Equal(1, session.Revisions[0].Turns.Count);
            await fixture.Service.RegenerateAsync(session, default); Equal(7, session.ApiCallCount);
            Equal(7, fixture.Handler.Calls); Equal(0, session.Revisions[0].Turns.Count);
        });
        yield return ("Third version rejects duplicate candidates rather than accepting three copies", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(Reply(call == 1 ? Direct() :
                JsonSerializer.Serialize(new { candidates = new[] { Greeting(0), Greeting(0), Greeting(0) } }))));
            var session = await fixture.ExtractAsync();
            await RejectAsync(() => fixture.Service.GenerateAsync(session, null, false, default));
            Equal(3, session.ApiCallCount); Equal(0, session.Candidates.Count);
        });
        yield return ("Third version refuses fabricated capture references and title evidence mislabeled as direct", async () =>
        {
            foreach (var bad in new[] {
                JsonSerializer.Serialize(new { name = "周老师", basis = "direct", evidence = "周老师，您好", evidence_items = new[] {
                    new { source = "chat_message", speaker = "self", quote = "周老师，您好", capture_id = "not-provided" } } }),
                JsonSerializer.Serialize(new { name = "周老师", basis = "direct", evidence = "联系人显示名称：周老师", evidence_items = new[] {
                    new { source = "chat_title", speaker = "unknown", quote = "周老师", capture_id = "capture_0" } } }) })
            {
                using var fixture = new Fixture(root, (_, _, _) => Task.FromResult(Reply(bad)));
                await RejectAsync(() => fixture.ExtractAsync()); Equal(2, fixture.Handler.Calls);
            }
        });
        yield return ("Third version unauthorized HTTP response is not retried and leaves candidates unchanged", async () =>
        {
            using var fixture = new Fixture(root, (call, _, _) => Task.FromResult(call == 1 ? Reply(Direct()) : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            var session = await fixture.ExtractAsync();
            await RejectAsync(() => fixture.Service.GenerateAsync(session, null, false, default));
            Equal(2, fixture.Handler.Calls); Equal(2, session.ApiCallCount); Equal(0, session.Candidates.Count);
        });
        yield return ("Third version refuses an unconsented model origin before any HTTP request", async () =>
        {
            using var fixture = new Fixture(root, (_, _, _) => throw new Exception("Unconsented HTTP request escaped validation."));
            await RejectAsync(() => fixture.ExtractAsync(Settings() with { AuthorizedOrigin = "https://other.example" }));
            Equal(0, fixture.Handler.Calls);
        });
        yield return ("Third version cancellation before and during HTTP never retries or publishes partial candidates", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            using var fixture = new Fixture(root, async (call, _, ct) =>
            {
                if (call == 1) return Reply(Direct());
                cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); throw new Exception("Cancelled HTTP completed.");
            });
            var session = await fixture.ExtractAsync();
            using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
            await CancelledAsync(() => fixture.Service.GenerateAsync(session, null, false, alreadyCancelled.Token));
            Equal(1, fixture.Handler.Calls); Equal(1, session.ApiCallCount);
            await CancelledAsync(() => fixture.Service.GenerateAsync(session, null, false, cancellation.Token));
            Equal(2, fixture.Handler.Calls); Equal(2, session.ApiCallCount); Equal(0, session.Candidates.Count);
        });
    }

    private static AppSettings Settings() => new() { DemoMode = false, Scene = "春节", Model = "offline-test-model",
        BaseUrl = "https://third-version.invalid/v1", AuthorizedOrigin = "https://third-version.invalid", RequestTimeoutSeconds = 5, TaskTimeoutSeconds = 15 };
    private static string Greeting(int index, string? address = "周老师") => (string.IsNullOrEmpty(address) ? "" : address + "，") + Texts[index];
    private static string Candidates(int offset = 0, string? address = "周老师") => JsonSerializer.Serialize(new { candidates = Enumerable.Range(offset, 3).Select(i => Greeting(i, address)) });
    private static string Revision(int index, string note = "") => JsonSerializer.Serialize(new { greeting = Greeting(index), note });
    private static string Unknown() => JsonSerializer.Serialize(new { name = (string?)null, basis = "unknown", evidence = (string?)null, evidence_items = Array.Empty<object>() });
    private static string Direct() => JsonSerializer.Serialize(new { name = "周老师", basis = "direct", evidence = "周老师，您好", evidence_items = new[] {
        new { source = "chat_message", speaker = "self", quote = "周老师，您好", capture_id = "capture_0" } } });
    private static string Inferred(string? observed = null) => JsonSerializer.Serialize(new { name = "妹妹", basis = "inferred", evidence = "联系人显示名称：妹妹；对方正文：哥哥，你这周回来吗？", observed_display_name = observed,
        evidence_items = new[] { new { source = "chat_title", speaker = "unknown", quote = "妹妹", capture_id = "capture_0" },
            new { source = "chat_message", speaker = "other", quote = "哥哥，你这周回来吗？", capture_id = "capture_0" } } });
    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
        choices = new[] { new { message = new { content } } } }), Encoding.UTF8, "application/json") };
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
    private static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException) { return; }
        throw new Exception("Expected an explicit validation or HTTP failure.");
    }
    private static async Task CancelledAsync(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation.");
    }

    private sealed class Fixture : IDisposable
    {
        internal string PromptDirectory { get; }
        internal RecordingHandler Handler { get; }
        internal ThirdVersionService Service { get; }
        internal Fixture(string root, Func<int, string, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            PromptDirectory = Path.Combine(root, "third-version-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PromptDirectory);
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config/third-version"));
            foreach (var name in new[] { "extract.txt", "generate.txt", "revise.txt", "examples.txt" })
                File.Copy(Path.Combine(source, name), Path.Combine(PromptDirectory, name));
            Handler = new RecordingHandler(respond); Service = new ThirdVersionService(PromptDirectory, Handler);
        }
        internal Task<ThirdVersionSession> ExtractAsync(AppSettings? settings = null, IReadOnlyList<Capture>? captures = null,
            Contact? contact = null, GreetingGenerationContext? context = null) => Service.ExtractAsync(settings ?? Settings(), "offline-placeholder-not-a-real-key",
                captures ?? [new("capture_0", [1, 2, 3])], contact, context ?? new([]), default);
        public void Dispose() => Service.Dispose();
    }

    private sealed class RecordingHandler(Func<int, string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Equal("third-version.invalid", request.RequestUri!.Host);
            Equal("Bearer", request.Headers.Authorization?.Scheme);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            // Decode escaped Chinese for readable, schema-independent prompt assertions.
            using var json = JsonDocument.Parse(body);
            body = JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            Bodies.Add(body); Calls++;
            return await respond(Calls, body, cancellationToken);
        }
    }
}
