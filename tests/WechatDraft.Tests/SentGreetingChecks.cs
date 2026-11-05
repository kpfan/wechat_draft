using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WechatDraft.Core;
using WechatDraft.Windows;

internal static class SentGreetingChecks
{
    private static readonly AppSettings Configuration = new() { DemoMode = false, BaseUrl = "https://test.example/v1",
        AuthorizedOrigin = "https://test.example", Model = "fake-vision" };
    private static SentGreetingResponse Good(string text = "祝你生日快乐！") => new()
    {
        SchemaVersion = "1.0", BeforeTitle = "测试联系人", AfterTitle = "测试联系人", NewOutgoingCount = 1,
        IsBlessing = true, TextComplete = true, Uncertain = false, SendFailed = false, MessageText = text
    };
    private static SendObservation Observation() => new(Guid.NewGuid().ToString("N"), "session", "测试联系人",
        DateTimeOffset.UtcNow, "鼠标发送", new("before", [1]), new("after", [2]));

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root, ISecretProtector protector)
    {
        LocalRepository Repository() => new(Path.Combine(root, "sent-" + Guid.NewGuid().ToString("N")), protector);
        yield return ("Self-written sent blessings are archived without a generation task and preserve final text", async () =>
        {
            var repo = Repository(); const string text = "  我自己写的祝福：\r\n愿你新岁安康，事事顺心🎂！  ";
            var model = new FakeSentModel(Good(text));
            var record = await new SentGreetingArchiver(repo, protector, model).ProcessAsync(Configuration, Observation(), default);
            Equal("已记录", record!.Status); Equal(text, repo.SentGreetings("default").Single().Text);
            Equal(0, repo.History("default").Count); Equal(1, repo.Contacts("default").Count);
            Equal(record.ContactId, repo.Contacts("default").Single().Id); Equal(1, model.Calls);
        });
        yield return ("Edited sent text does not overwrite the original generated blessing", async () =>
        {
            var repo = Repository(); var task = new TaskRecord { Greeting = "模型原稿", CompletedAt = DateTimeOffset.UtcNow };
            repo.SaveTask(task);
            var result = await new SentGreetingArchiver(repo, protector, new FakeSentModel(Good("愿你每天开心，生日快乐！")))
                .ProcessAsync(Configuration, Observation(), default);
            Equal("愿你每天开心，生日快乐！", result!.Text); Equal("模型原稿", repo.History("default").Single().Greeting);
        });
        yield return ("Concurrent duplicate send observations issue one model request and create one record", async () =>
        {
            var repo = Repository(); var model = new FakeSentModel(Good()) { Delay = 25 };
            var archiver = new SentGreetingArchiver(repo, protector, model); var observation = Observation();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => archiver.ProcessAsync(Configuration, observation, default)));
            Equal(1, model.Calls); Equal(1, repo.SentGreetings("default").Count);
        });
        yield return ("Separate sends of identical text remain separate records", async () =>
        {
            var repo = Repository(); var model = new FakeSentModel(Good()); var archiver = new SentGreetingArchiver(repo, protector, model);
            await archiver.ProcessAsync(Configuration, Observation(), default);
            await archiver.ProcessAsync(Configuration, Observation(), default);
            Equal(2, model.Calls); Equal(2, repo.SentGreetings("default").Count); Equal(1, repo.Contacts("default").Count);
        });
        yield return ("Sent records and contact matching stay inside the current account and exclude demo contacts", async () =>
        {
            var repo = Repository();
            var other = new Contact { AccountScope = "other", DisplayName = "测试联系人", Confirmed = true };
            var demo = new Contact { DisplayName = "测试联系人", IsDemo = true, Confirmed = true };
            repo.SaveContact(other); repo.SaveContact(demo);
            var record = await new SentGreetingArchiver(repo, protector, new FakeSentModel(Good())).ProcessAsync(Configuration, Observation(), default);
            True(record!.ContactId != other.Id && record.ContactId != demo.Id, "cross-account/demo binding");
            Equal(0, repo.SentGreetings("other").Count);
        });
        yield return ("Incoming and non-blessing messages never retain body text or create contacts", async () =>
        {
            foreach (var response in new[] { Good("should-not-be-saved") with { NewOutgoingCount = 0 }, Good("普通聊天") with { IsBlessing = false } })
            {
                var repo = Repository();
                var result = await new SentGreetingArchiver(repo, protector, new FakeSentModel(response)).ProcessAsync(Configuration, Observation(), default);
                Equal("无需归档", result!.Status); Equal("", result.Text); Equal(0, repo.Contacts("default").Count);
            }
        });
        yield return ("Uncertain, failed, multiple, mismatched, or truncated sent messages remain unconfirmed", async () =>
        {
            foreach (var response in new[]
            {
                Good() with { Uncertain = true }, Good() with { SendFailed = true }, Good() with { NewOutgoingCount = 2 },
                Good() with { TextComplete = false }, Good() with { BeforeTitle = "其他人" }, Good() with { AfterTitle = "其他人" },
                Good() with { SchemaVersion = "2.0" }, Good() with { NewOutgoingCount = -1 }, Good(""), Good("bad\0body"),
                Good(new string('字', 8001))
            })
            {
                var repo = Repository(); var model = new FakeSentModel(response);
                var record = await new SentGreetingArchiver(repo, protector, model).ProcessAsync(Configuration, Observation(), default);
                Equal("待确认", record!.Status); Equal("", record.Text); Equal(null, record.VerifiedAt);
                Equal(0, repo.Contacts("default").Count); Equal(1, model.Calls);
            }
        });
        yield return ("Unchanged chat pictures do not request a model or create a sent record", async () =>
        {
            var repo = Repository(); var model = new FakeSentModel(Good()); var observation = Observation();
            var result = await new SentGreetingArchiver(repo, protector, model).ProcessAsync(Configuration,
                observation with { After = observation.Before }, default);
            Equal(null, result); Equal(0, model.Calls); Equal(0, repo.SentGreetings("default").Count);
        });
        yield return ("Interrupted local send observations remain visible without uploading screenshots", async () =>
        {
            var repo = Repository(); var model = new FakeSentModel(Good()); var observation = Observation();
            var record = await new SentGreetingArchiver(repo, protector, model).ProcessAsync(Configuration,
                observation with { After = observation.Before, LocalUncertainty = "发送后切换了窗口" }, default);
            Equal("待确认", record!.Status); Equal(0, model.Calls); Equal(0, record.ApiCallCount);
        });
        yield return ("Cancelled model verification is retained without retrying the send observation", async () =>
        {
            var repo = Repository(); var model = new FakeSentModel(Good()) { Delay = 5000 };
            var archiver = new SentGreetingArchiver(repo, protector, model); var observation = Observation();
            using var cancellation = new CancellationTokenSource();
            var processing = archiver.ProcessAsync(Configuration, observation, cancellation.Token);
            cancellation.Cancel();
            Equal("待确认", (await processing)!.Status);
            await archiver.ProcessAsync(Configuration, observation, default);
            Equal(1, model.Calls);
        });
        yield return ("Model or parse failures never replay uncertain observations", async () =>
        {
            foreach (var error in new Exception[] { new JsonException(), new TransientApiException("temporary") })
            {
                var repo = Repository(); var model = new FakeSentModel(Good()) { Error = error };
                var archiver = new SentGreetingArchiver(repo, protector, model); var observation = Observation();
                Equal("待确认", (await archiver.ProcessAsync(Configuration, observation, default))!.Status);
                await archiver.ProcessAsync(Configuration, observation, default); Equal(1, model.Calls);
            }
        });
        yield return ("Deletion while a sent-message request is in flight does not resurrect a record", async () =>
        {
            var repo = Repository(); var completion = new TaskCompletionSource<SentGreetingResponse>();
            var model = new FakeSentModel(Good()) { Completion = completion }; var observation = Observation();
            var processing = new SentGreetingArchiver(repo, protector, model).ProcessAsync(Configuration, observation, default);
            repo.DeleteSentGreeting(observation.Id); completion.SetResult(Good());
            Equal(null, await processing); Equal(0, repo.SentGreetings("default").Count); Equal(0, repo.Contacts("default").Count);
        });
        yield return ("A late model response after cancellation cannot archive text or create a contact", async () =>
        {
            var repo = Repository(); var completion = new TaskCompletionSource<SentGreetingResponse>();
            var model = new FakeSentModel(Good()) { Completion = completion, IgnoreCancellation = true };
            using var cancellation = new CancellationTokenSource();
            var processing = new SentGreetingArchiver(repo, protector, model).ProcessAsync(Configuration, Observation(), cancellation.Token);
            cancellation.Cancel(); completion.SetResult(Good());
            var record = await processing;
            Equal("待确认", record!.Status); Equal("", record.Text); Equal(0, repo.Contacts("default").Count);
        });
        yield return ("A send observation ID cannot return records from another account scope", async () =>
        {
            var repo = Repository(); var observation = Observation(); var model = new FakeSentModel(Good());
            var archiver = new SentGreetingArchiver(repo, protector, model);
            await archiver.ProcessAsync(Configuration, observation, default);
            await RejectAsync(() => archiver.ProcessAsync(Configuration with { AccountScope = "other" }, observation, default));
            Equal(1, model.Calls); Equal(0, repo.SentGreetings("other").Count);
        });
        yield return ("Failure to persist the initial sent observation prevents a model upload", async () =>
        {
            var repo = new LocalRepository(Path.Combine(root, "sent-no-storage-" + Guid.NewGuid().ToString("N")), new FailingProtector());
            var model = new FakeSentModel(Good());
            await RejectAsync(() => new SentGreetingArchiver(repo, protector, model).ProcessAsync(Configuration, Observation(), default));
            Equal(0, model.Calls);
        });
        yield return ("Send verification validates screenshot authorization and limits before any request", async () =>
        {
            var repo = Repository(); var model = new FakeSentModel(Good()); var archiver = new SentGreetingArchiver(repo, protector, model);
            await RejectAsync(() => archiver.ProcessAsync(Configuration with { AuthorizedOrigin = "https://other.example" }, Observation(), default));
            await RejectAsync(() => archiver.ProcessAsync(Configuration with { DemoMode = true }, Observation(), default));
            await RejectAsync(() => archiver.ProcessAsync(Configuration, Observation() with { Before = new("large", new byte[12 * 1024 * 1024]) }, default));
            Equal(0, model.Calls); Equal(0, repo.SentGreetings("default").Count);
        });
        yield return ("Sent payloads are encrypted, recover interrupted verification, and respect retention and scope", () =>
        {
            var folder = Path.Combine(root, "sent-encrypted-" + Guid.NewGuid().ToString("N")); var repo = new LocalRepository(folder, protector);
            var record = new SentGreetingRecord { Text = "private-sent-text-随机", DisplayName = "private-name", Status = "核对中" };
            repo.SaveSentGreeting(record); repo.RecoverInterrupted(); Equal("待确认", repo.FindSentGreeting(record.Id)!.Status);
            using (var db = new SqliteConnection("Data Source=" + Path.Combine(folder, "drafts.db")))
            {
                db.Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM sent_greetings";
                var payload = (string)command.ExecuteScalar()!;
                True(!payload.Contains(record.Text) && !payload.Contains(record.DisplayName), "unencrypted sent data");
            }
            repo.SaveSentGreeting(new() { AccountScope = "other", Status = "已记录" });
            repo.ClearSentGreetings("default"); Equal(1, repo.SentGreetings("other").Count); Equal(0, repo.SentGreetings("default").Count);
            repo.SaveSentGreeting(new() { AccountScope = "other", Status = "已记录", ObservedAt = DateTimeOffset.UtcNow.AddDays(-100) });
            repo.Cleanup(90); Equal(1, repo.SentGreetings("other").Count);
            return Task.CompletedTask;
        });
        yield return ("Deleting a contact optionally cascades to its sent blessings", () =>
        {
            var repo = Repository(); var contact = new Contact(); repo.SaveContact(contact);
            repo.SaveSentGreeting(new() { ContactId = contact.Id, Text = "祝福", Status = "已记录" });
            repo.DeleteContact(contact.Id, false); Equal(1, repo.SentGreetings("default").Count);
            repo.SaveContact(contact); repo.DeleteContact(contact.Id, true); Equal(0, repo.SentGreetings("default").Count);
            return Task.CompletedTask;
        });
        yield return ("Send gestures require a complete target click and suppress held Enter and injected input", () =>
        {
            var gestures = new SendGestureState();
            True(!gestures.Mouse(true, true, true, false), "down must not send");
            True(gestures.Mouse(false, true, true, false), "valid click lost");
            True(!gestures.Mouse(false, true, true, false), "duplicate release");
            gestures.Mouse(true, false, true, false); True(!gestures.Mouse(false, true, true, false), "drag into send");
            gestures.Mouse(true, true, true, false); True(!gestures.Mouse(false, false, true, false), "release outside send");
            gestures.Mouse(true, true, true, true); True(!gestures.Mouse(false, true, true, true), "injected click");
            gestures.Mouse(true, true, true, false); True(!gestures.Mouse(false, true, false, false), "wrong foreground");
            True(gestures.Enter(true, true, true, false, false), "Enter not observed");
            True(!gestures.Enter(true, true, true, false, false), "held Enter repeated");
            gestures.Enter(false, true, true, false, false); True(gestures.Enter(true, true, true, false, false), "second Enter lost");
            foreach (var flags in new[] { (false, true, false, false), (true, false, false, false), (true, true, true, false), (true, true, false, true) })
            { gestures.Reset(); True(!gestures.Enter(true, flags.Item1, flags.Item2, flags.Item3, flags.Item4), "unsafe Enter accepted"); }
            return Task.CompletedTask;
        });
        yield return ("Sent-message protocol sends only two observation pictures and rejects missing safety fields", async () =>
        {
            var handler = new ResponseHandler(request =>
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                True(!body.RootElement.TryGetProperty("tools", out _), "observation exposed draft tools");
                var content = body.RootElement.GetProperty("messages")[1].GetProperty("content");
                Equal(2, content.EnumerateArray().Count(x => x.GetProperty("type").GetString() == "image_url"));
                True(!body.RootElement.GetRawText().Contains("confirmed_contact"), "generation data included");
                return Response(JsonSerializer.Serialize(Good("自己写的生日快乐！")));
            });
            using var client = new CompatibleModelClient(handler);
            var result = await client.ReadSentGreetingAsync(Configuration, "", Observation(), default);
            Equal("自己写的生日快乐！", result.MessageText); Equal(1, handler.Calls);
            using var incomplete = new CompatibleModelClient(new ResponseHandler(_ => Response("{\"schema_version\":\"1.0\"}")));
            await RejectAsync(() => incomplete.ReadSentGreetingAsync(Configuration, "", Observation(), default));
            using var function = new CompatibleModelClient(new ResponseHandler(_ => new(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"tool_calls\":[{\"function\":{\"name\":\"submit_draft\",\"arguments\":\"{}\"}}]}}]}") }));
            await RejectAsync(() => function.ReadSentGreetingAsync(Configuration, "", Observation(), default));
        });
    }

    private static HttpResponseMessage Response(string content) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } })) };
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidDataException or JsonException) { return; }
        throw new Exception("Expected rejection");
    }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
    private static void True(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class FakeSentModel(SentGreetingResponse response) : ISentGreetingModel
    {
        public int Calls; public int Delay; public bool IgnoreCancellation; public Exception? Error; public TaskCompletionSource<SentGreetingResponse>? Completion;
        public async Task<SentGreetingResponse> ReadSentGreetingAsync(AppSettings settings, string key, SendObservation observation, CancellationToken ct)
        {
            Calls++;
            if (Delay > 0) await Task.Delay(Delay, ct);
            if (Error is not null) throw Error;
            return Completion is null ? response : IgnoreCancellation ? await Completion.Task : await Completion.Task.WaitAsync(ct);
        }
    }
    private sealed class FailingProtector : ISecretProtector
    {
        public string Protect(string value) => throw new InvalidDataException("storage unavailable");
        public string Unprotect(string value) => throw new InvalidDataException("storage unavailable");
    }
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(respond(request)); }
    }
}
