using System.Net;
using System.Text.Json;
using WechatDraft.Core;

internal static class GreetingDiversityChecks
{
    private const string Old = "生日快乐！愿你新的一岁平安顺心，每个平常的日子里都有值得期待的小事。";
    private const string Fresh = "生日快乐！今天把忙碌暂放一旁，好好吃顿喜欢的饭，痛快笑一场，愿往后的生活自在又明亮。";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root, ExampleService examples, ISecretProtector protector)
    {
        LocalRepository Repository() => new(Path.Combine(root, "variety-" + Guid.NewGuid().ToString("N")), protector);
        var settings = new AppSettings { DemoMode = false, Scene = "生日", EnableLiveAppend = true,
            BaseUrl = "https://test.example/v1", AuthorizedOrigin = "https://test.example", Model = "fake" };
        void Seed(LocalRepository repo, string text = Old) => repo.SaveTask(new() { DisplayName = "测试联系人", Scene = "生日",
            Greeting = text, GenerationStatus = "成功", CompletedAt = DateTimeOffset.UtcNow });
        yield return ("Repeated greetings ignore punctuation or emoji changes and reject minor rewrites without rejecting new wishes", () =>
        {
            if (!GreetingDiversity.TooSimilar("小林，新春快乐！愿你新的一年万事顺遂，日常多有轻松喜悦，平安健康，事事顺心。",
                "小林，新春快乐！祝你春节阖家团圆，万事顺遂，日常多有轻松喜悦，平安健康，事事顺心。", "小林") ||
                !GreetingDiversity.TooSimilar(Old, Old.Replace("！", "! 😊").Replace("，", " ")) || GreetingDiversity.TooSimilar(Old, Fresh))
                throw new Exception("Greeting similarity boundaries failed.");
            return Task.CompletedTask;
        });
        yield return ("Recent greeting lookup isolates account, contact, scene and mode, deduplicates and limits output", () =>
        {
            var repo = Repository(); var started = DateTimeOffset.UtcNow;
            for (var i = 0; i < 8; i++) repo.SaveTask(new() { Id = "wanted-" + i, AccountScope = "a", ContactId = "c",
                DisplayName = "测试", Scene = "春节", Greeting = "不同祝福" + i, GenerationStatus = "成功", StartedAt = started.AddSeconds(-i) });
            repo.SaveTask(new() { AccountScope = "a", ContactId = "c", DisplayName = "测试", Scene = "春节", Greeting = "不同祝福0！", GenerationStatus = "成功" });
            foreach (var other in new[] {
                new TaskRecord { AccountScope = "b", ContactId = "c", DisplayName = "测试", Scene = "春节", GenerationStatus = "成功" },
                new TaskRecord { AccountScope = "a", ContactId = "other", DisplayName = "测试", Scene = "春节", GenerationStatus = "成功" },
                new TaskRecord { AccountScope = "a", ContactId = "c", DisplayName = "测试", Scene = "生日", GenerationStatus = "成功" },
                new TaskRecord { AccountScope = "a", ContactId = "c", DisplayName = "测试", Scene = "春节", GenerationStatus = "失败" },
                new TaskRecord { AccountScope = "a", ContactId = "c", DisplayName = "测试", Scene = "春节", GenerationStatus = "成功", IsDemo = true } })
            { other.Greeting = "不应提供给本次生成"; repo.SaveTask(other); }
            var result = repo.RecentGreetings("a", "c", "测试", "春节", false);
            if (result.Count != 5 || result.Any(text => text.Contains("不应")) ||
                result.Select(text => GreetingDiversity.Normalize(text)).Distinct().Count() != 5)
                throw new Exception("Unrelated or excess history entered generation.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { repo.RecentGreetings("a", "c", "测试", "春节", false, cancelled.Token); }
            catch (OperationCanceledException) { return Task.CompletedTask; }
            throw new Exception("Cancelled lookup was not stopped.");
        });
        yield return ("A repeated candidate is rewritten once before any append and receives recent results and the rejected draft", async () =>
        {
            var repo = Repository(); Seed(repo); var adapter = new FakeAdapter { IsDemo = false };
            var model = new SequenceModel([Old, Fresh]);
            var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, model, null, default);
            if (result!.Greeting != Fresh || result.ApiCallCount != 2 || adapter.Appends != 1 ||
                !model.Contexts[0].RecentGreetings.Contains(Old) || model.Contexts[1].RejectedCandidate != Old)
                throw new Exception("Repeated output was appended or rewrite guidance was missing.");
        });
        yield return ("Two repeated candidates fail clearly and never create an append intent or reuse an old result", async () =>
        {
            var repo = Repository(); Seed(repo); var adapter = new FakeAdapter { IsDemo = false }; var model = new SequenceModel([Old, Old]);
            var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, model, null, default);
            if (result!.GenerationStatus != "失败" || result.Greeting.Length != 0 || model.Calls != 2 || adapter.Appends != 0 ||
                result.AppendAttemptId is not null || !result.Error.Contains("过于相似")) throw new Exception("Repeated output reached a write.");
        });
        yield return ("Copying a saved salutation evidence quote triggers a rewrite while its local evidence stays intact", async () =>
        {
            var repo = Repository(); var quote = "陈老师，" + Old;
            var contact = new Contact { DisplayName = "测试联系人", AddressTerm = "陈老师", AddressTermSource = "chat_message",
                AddressEvidence = new() { Source = "chat_message", Speaker = "self", Quote = quote, CaptureId = "c" } };
            repo.SaveContact(contact);
            var model = new SequenceModel([quote, "陈老师，" + Fresh], "陈老师");
            var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, new FakeAdapter { IsDemo = false }, model, null, default);
            if (result!.Greeting != "陈老师，" + Fresh || model.Calls != 2 || repo.Contacts("default").Single().AddressEvidence!.Quote != quote)
                throw new Exception("Evidence was copied as a greeting or local evidence was overwritten.");
        });
        yield return ("Rewriting shares the existing single extra request budget with network retries", async () =>
        {
            var repo = Repository(); Seed(repo); var adapter = new FakeAdapter { IsDemo = false };
            var model = new SequenceModel([Old], transientFirst: true);
            var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, model, null, default);
            if (result!.ApiCallCount != 2 || model.Calls != 2 || adapter.Appends != 0 || result.GenerationStatus != "失败")
                throw new Exception("Quality rewrite exceeded the shared request limit.");
        });
        yield return ("Generation sends the validated address but not its full evidence quote and labels past greetings only as exclusions", async () =>
        {
            const string quote = "陈老师，这是用于证明称呼的私人长摘录，不应被作为正文范本发到生成请求里。";
            var contact = new Contact { DisplayName = "测试联系人", AddressTerm = "陈老师", AddressTermSource = "chat_message",
                AddressEvidence = new() { Source = "chat_message", Speaker = "self", Quote = quote, CaptureId = "c" } };
            var handler = new StubHandler(request =>
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var messages = body.RootElement.GetProperty("messages");
                var instruction = messages[1].GetProperty("content")[0].GetProperty("text").GetString()!;
                using var json = JsonDocument.Parse(instruction);
                if (instruction.Contains(quote) || json.RootElement.GetProperty("stored_contact").TryGetProperty("addressEvidence", out _) ||
                    json.RootElement.GetProperty("salutation").GetProperty("required_address_term").GetString() != "陈老师" ||
                    json.RootElement.GetProperty("variation").GetProperty("recent_greetings")[0].GetString() != Old ||
                    json.RootElement.GetProperty("variation").GetProperty("rejected_candidate").GetString() != Old ||
                    !messages[0].GetProperty("content").GetString()!.Contains("只用于避开重复")) throw new Exception("Prompt evidence/variation separation failed.");
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new {
                    content = JsonSerializer.Serialize(new ModelResponse { GreetingText = "陈老师，" + Fresh }) } } } })) };
            });
            using var client = new CompatibleModelClient(handler);
            await client.GenerateAsync(settings, "", [new("c", [1])], contact, [], true, new([Old], Old), default);
            if (contact.AddressEvidence.Quote != quote) throw new Exception("Preparing the request changed local evidence.");
        });
    }

    private sealed class SequenceModel(string[] greetings, string? address = null, bool transientFirst = false) : IGreetingModel
    {
        public int Calls;
        public List<GreetingGenerationContext> Contexts { get; } = [];
        public Task<ModelResponse> GenerateAsync(AppSettings s, string key, IReadOnlyList<Capture> captures, Contact? c,
            IReadOnlyList<BlessingExample> e, bool final, CancellationToken ct) => throw new Exception("Generation context was lost.");
        public Task<ModelResponse> GenerateAsync(AppSettings s, string key, IReadOnlyList<Capture> captures, Contact? c,
            IReadOnlyList<BlessingExample> e, bool final, GreetingGenerationContext context, CancellationToken ct)
        {
            Calls++; Contexts.Add(context);
            if (transientFirst && Calls == 1) throw new TransientApiException("test", TimeSpan.Zero);
            return Task.FromResult(new ModelResponse { ContextSufficient = true, ObservedDisplayName = "测试联系人", AddressTerm = address,
                GreetingText = greetings[Math.Min(greetings.Length - 1, Calls - 1 - (transientFirst ? 1 : 0))] });
        }
    }
}
