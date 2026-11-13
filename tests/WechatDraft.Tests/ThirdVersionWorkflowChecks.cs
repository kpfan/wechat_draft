using System.Net;
using System.Text;
using System.Text.Json;
using WechatDraft.Core;
using WechatDraft.Core.ThirdVersion;

internal static class ThirdVersionWorkflowChecks
{
    private const int MaxContextCaptures = 3;
    private static readonly string[] Greetings =
    [
        "周老师，新春快乐！愿新的一年工作有清晰方向，生活多些轻松自在，忙碌之余也能安心吃饭、踏实休息，和家人共享温暖时光。[庆祝][福]",
        "周老师，春节给您拜年了！祝身体康健，阖家和乐，前行路上收获丰盛，闲暇日子随心安排，常有值得期待的惊喜相伴。[烟花][爆竹]",
        "周老师，过年好！把真诚的惦念装进这声问候，愿每次努力都有回响，每段旅程皆见风景，保持热情和笃定，把平凡日子过得明亮充实。[福][庆祝]",
        "周老师，新年好！岁序更新，送上朴实祝愿：冷暖适宜，饮食可口，精神饱满，心中安宁，愿您遇见志趣相投的人，在珍惜的领域发现乐趣，所有美好慢慢抵达。[微笑][福]",
        "周老师，春节到了，祝您万事顺心！愿手中的计划稳步推进，尚未决定的方向渐渐清晰，走过四季仍能保有好奇，守护自己的节奏，留住每个值得珍惜的瞬间。[庆祝][烟花]",
        "周老师，新春送暖，愿您平安！窗外春意带来希望，家中灯火温暖可亲，案头事务井然有序；也愿您照顾好自己，在晨昏之间积攒幸福，往后一路从容坦然。[福][微笑]",
        "周老师，祝春节愉快！愿新的篇章少些烦忧，多些欢笑，暂时的曲折不减前行信心，独处时自在，团聚时尽兴，把健康与快乐好好留在身边。[庆祝][福]"
    ];

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string root, ISecretProtector protector)
    {
        yield return ("Third-version workflow captures the open chat automatically and never appends before explicit selection", async () =>
        {
            await using var f = new Fixture(root, protector);
            var run = await f.StartAsync();
            Equal(1, f.Adapter.Begins); Equal(MaxContextCaptures, f.Adapter.Captures);
            Equal(MaxContextCaptures - 1, f.Adapter.Scrolls);
            Equal(3, run.Session!.Candidates.Count); Equal(2, run.Record.ApiCallCount); Equal(2, f.Handler.Calls);
            Equal(-1, run.SelectedIndex); Equal(0, f.Adapter.Appends); Equal(" 原有草稿\r\n", f.Adapter.CurrentDraft);
            f.Workflow.Select(run, 1);
            Equal(1, run.SelectedIndex); Equal(Greetings[1], run.Record.Greeting); Equal(0, f.Adapter.Appends);
        });
        yield return ("Third-version workflow persists selected append intent before one original-baseline append", async () =>
        {
            await using var f = new Fixture(root, protector);
            var run = await f.StartAsync(); f.Workflow.Select(run, 2);
            f.Adapter.BeforeAppend = snapshot =>
            {
                var stored = f.Repository.History("default").Single(x => x.Id == run.Record.Id);
                Equal("写入中", stored.FillStatus); True(!string.IsNullOrEmpty(stored.AppendAttemptId), "Append intent was not durable.");
                Equal(Greetings[2], stored.Greeting); Equal(" 原有草稿\r\n", snapshot.OriginalText);
                True(ReferenceEquals(f.Adapter.Baseline, snapshot), "Append reconstructed the original snapshot.");
            };
            var record = await f.Workflow.AppendSelectedAsync(run, default);
            Equal(1, f.Adapter.Appends); Equal("已预填", record.FillStatus);
            Equal(DraftRules.Append(" 原有草稿\r\n", Greetings[2]), f.Adapter.CurrentDraft);
            await f.Workflow.AppendSelectedAsync(run, default); Equal(1, f.Adapter.Appends);
        });
        yield return ("Third-version workflow cannot append without a selected candidate", async () =>
        {
            await using var f = new Fixture(root, protector); var run = await f.StartAsync();
            await f.Workflow.AppendSelectedAsync(run, default);
            Equal(0, f.Adapter.Appends); Equal<string?>(null, run.Record.AppendAttemptId);
        });
        yield return ("Third-version workflow collects the upstream bounded context before one unknown extraction and pauses without generation", async () =>
        {
            await using var f = new Fixture(root, protector, unknownExtractions: int.MaxValue);
            var run = await f.StartAsync();
            Equal(MaxContextCaptures, f.Adapter.Captures); Equal(MaxContextCaptures - 1, f.Adapter.Scrolls);
            Equal(1, f.Handler.Extractions); Equal(0, f.Handler.Generations);
            Equal(MaxContextCaptures, run.Record.CaptureCount); Equal(MaxContextCaptures - 1, run.Record.ScrollCount);
            Equal(1, run.Record.ApiCallCount);
            Equal("unknown", run.Session!.Extraction.Basis); Equal(0, run.Session.Candidates.Count); Equal(0, f.Adapter.Appends);
        });
        yield return ("Third-version workflow stops extra capture on adapter capability, failed scroll or duplicate frames", async () =>
        {
            foreach (var reason in new[] { "capability", "scroll", "duplicate" })
            {
                await using var f = new Fixture(root, protector, unknownExtractions: int.MaxValue);
                f.Adapter.CanCollectMore = reason != "capability"; f.Adapter.ScrollSucceeds = reason != "scroll";
                f.Adapter.DuplicateFrames = reason == "duplicate";
                var run = await f.StartAsync();
                Equal(reason == "duplicate" ? 2 : 1, f.Adapter.Captures);
                Equal(reason == "capability" ? 0 : 1, f.Adapter.Scrolls);
                Equal(1, f.Handler.Extractions); Equal(0, f.Handler.Generations); Equal(1, run.Record.CaptureCount);
            }
        });
        yield return ("Third-version workflow known address still collects the upstream context budget before the first model request", async () =>
        {
            await using var f = new Fixture(root, protector);
            f.Handler.BeforeRequest = () =>
            {
                Equal(MaxContextCaptures, f.Adapter.Captures);
                Equal(MaxContextCaptures - 1, f.Adapter.Scrolls);
            };
            var run = await f.StartAsync();
            Equal(MaxContextCaptures, run.Session!.Captures.Count);
            Equal(1, f.Handler.Extractions); Equal(1, f.Handler.Generations);
            Equal(2, run.Record.ApiCallCount); Equal(3, run.Session.Candidates.Count); Equal(0, f.Adapter.Appends);
        });
        yield return ("Third-version workflow unknown pause resumes with manual or explicitly neutral generation", async () =>
        {
            foreach (var neutral in new[] { false, true })
            {
                await using var f = new Fixture(root, protector, unknownExtractions: int.MaxValue);
                f.Adapter.CanCollectMore = false; var run = await f.StartAsync();
                Equal(0, run.Session!.Candidates.Count);
                await f.Workflow.GenerateAsync(run, neutral ? null : "周老师", neutral, default);
                Equal(3, run.Session.Candidates.Count); Equal(neutral, run.Session.Neutral);
                Equal(2, run.Record.ApiCallCount); Equal(1, f.Adapter.Captures); Equal(0, f.Adapter.Appends);
            }
        });
        yield return ("Third-version workflow keeps OCR title and capture metadata intact without model title re-identification", async () =>
        {
            await using var f = new Fixture(root, protector); f.Adapter.TitleSource = "ocr";
            f.Adapter.RequiresConversationCheck = true;
            var run = await f.StartAsync();
            Equal("周老师", run.Session!.Extraction.ObservedDisplayName);
            Equal("周老师", run.Session.Captures[0].LocalTitle); True(run.Session.Captures[0].RequiresConversationCheck, "Capture check metadata lost.");
            Equal("周老师", run.Record.DisplayName); Equal(0, f.Adapter.Appends);
        });
        yield return ("Third-version workflow rejects missing upload consent and group titles before model HTTP", async () =>
        {
            foreach (var group in new[] { false, true })
            {
                await using var f = new Fixture(root, protector);
                if (group) f.Adapter.Title = "同事群(8)"; else f.Settings.AuthorizedOrigin = "https://not-authorized.invalid";
                var run = await f.StartAsync();
                Equal(0, f.Handler.Calls); Equal(0, f.Adapter.Captures); Equal(0, f.Adapter.Appends);
                True(run.Record.Error.Length > 0, "Preflight rejection was not visible.");
            }
        });
        yield return ("Third-version workflow preserves confirmed blank contact as neutral after upstream context collection without mutating contact", async () =>
        {
            await using var f = new Fixture(root, protector);
            var contact = new Contact { DisplayName = "周老师", Confirmed = true, AddressTerm = "", Relationship = "unknown", IdentityLabel = "保留档案" };
            f.Repository.SaveContact(contact); var run = await f.StartAsync();
            Equal(MaxContextCaptures, f.Adapter.Captures); Equal(MaxContextCaptures - 1, f.Adapter.Scrolls);
            Equal(1, f.Handler.Extractions); Equal(3, run.Session!.Candidates.Count);
            True(run.Session.Neutral, "Confirmed blank contact was not generated neutrally.");
            True(run.Session.Candidates.All(x => !x.StartsWith("周老师", StringComparison.Ordinal)), "Neutral output invented an address.");
            var saved = f.Repository.Contacts("default").Single(); Equal("", saved.AddressTerm); Equal("unknown", saved.Relationship); Equal("保留档案", saved.IdentityLabel);
        });
        yield return ("Third-version workflow non-OCR model title mismatch retains candidates without write permission even with DirectAppend", async () =>
        {
            foreach (var directAppend in new[] { false, true })
            {
                await using var f = new Fixture(root, protector); f.Handler.ModelTitle = "其他联系人";
                f.Adapter.DirectAppend = directAppend;
                var run = await f.StartAsync(); Equal(3, run.Session!.Candidates.Count); f.Workflow.Select(run, 0);
                True(!run.CanAppend, "Mismatched model title granted append permission.");
                await f.Workflow.AppendSelectedAsync(run, default); Equal(0, f.Adapter.Appends); Equal<string?>(null, run.Record.AppendAttemptId);
            }
        });
        yield return ("Third-version workflow failed generation keeps extraction and counts both failed attempts", async () =>
        {
            await using var f = new Fixture(root, protector); f.Handler.InvalidGeneration = true;
            var run = await f.StartAsync();
            True(run.Session is not null, "Failed generation discarded a valid extraction."); Equal("direct", run.Session!.Extraction.Basis);
            Equal(0, run.Session.Candidates.Count); Equal(3, run.Record.ApiCallCount); Equal(3, f.Handler.Calls);
            Equal(0, f.Adapter.Appends); True(run.Record.Error.Length > 0, "Generation failure was not visible.");
        });
        yield return ("Third-version workflow revisions stay branched, reset originals and regeneration clears selection", async () =>
        {
            await using var f = new Fixture(root, protector); var run = await f.StartAsync();
            f.Workflow.Select(run, 0);
            var revision = await f.Workflow.ReviseAsync(run, 0, "第一分支修改", default);
            True(revision is not null, "Revision failed."); Equal(1, run.Session!.Revisions[0].Turns.Count);
            Equal(0, run.Session.Revisions[1].Turns.Count); Equal(Greetings[0], run.Session.Candidates[0]);
            f.Workflow.Reset(run, 0); Equal(Greetings[0], run.Session.Revisions[0].Greeting); Equal(0, run.Session.Revisions[0].Turns.Count);
            await f.Workflow.RegenerateAsync(run, default);
            Equal(-1, run.SelectedIndex); Equal(Greetings[3], run.Session.Candidates[0]);
            Equal(4, run.Record.ApiCallCount); Equal(0, f.Adapter.Appends);
        });
        yield return ("Third-version workflow changed draft, identity or focus blocks selected append without durable intent", async () =>
        {
            foreach (var change in new[] { "draft", "identity", "focus" })
            {
                await using var f = new Fixture(root, protector); var run = await f.StartAsync(); f.Workflow.Select(run, 0);
                if (change == "draft") f.Adapter.CurrentDraft += " ";
                else if (change == "identity") f.Adapter.CurrentIdentity = "other-window";
                else f.Adapter.Ready = false;
                await f.Workflow.AppendSelectedAsync(run, default);
                Equal(0, f.Adapter.Appends); Equal<string?>(null, run.Record.AppendAttemptId);
            }
        });
        yield return ("Third-version workflow restoring a changed baseline never restores append permission", async () =>
        {
            foreach (var change in new[] { "draft", "identity", "focus" })
            {
                await using var f = new Fixture(root, protector); var run = await f.StartAsync(); f.Workflow.Select(run, 0);
                if (change == "draft") f.Adapter.CurrentDraft += " ";
                else if (change == "identity") f.Adapter.CurrentIdentity = "other-window";
                else f.Adapter.Ready = false;
                await f.Workflow.AppendSelectedAsync(run, default);
                f.Adapter.CurrentDraft = " 原有草稿\r\n"; f.Adapter.CurrentIdentity = "original-window"; f.Adapter.Ready = true;
                True(!run.CanAppend, "Returning to the original state renewed revoked append permission.");
                await f.Workflow.AppendSelectedAsync(run, default);
                Equal(0, f.Adapter.Appends); Equal<string?>(null, run.Record.AppendAttemptId);
            }
        });
        yield return ("Third-version workflow canceled upstream collection never uploads partial context or retains write permission", async () =>
        {
            await using var f = new Fixture(root, protector, unknownExtractions: int.MaxValue);
            f.Adapter.CancelOnScroll = true; var run = await f.StartAsync();
            Equal<ThirdVersionSession?>(null, run.Session); Equal(0, f.Handler.Calls); Equal(0, run.Record.ApiCallCount);
            Equal(1, f.Adapter.Captures); Equal(1, f.Adapter.Scrolls);
            True(run.IsReleased, "Cancellation kept the original baseline alive.");
            Equal(1, f.Adapter.Restores); Equal(1, f.Adapter.Disposals);
            await f.Workflow.GenerateAsync(run, "周老师", false, default);
            Equal<ThirdVersionSession?>(null, run.Session); True(!run.CanAppend, "Canceled collection granted append permission.");
            await f.Workflow.AppendSelectedAsync(run, default);
            Equal(0, f.Adapter.Appends); Equal<string?>(null, run.Record.AppendAttemptId); Equal(0, f.Handler.Calls);
            Equal(1, f.Adapter.Restores); Equal(1, f.Adapter.Disposals);
        });
        yield return ("Third-version workflow ordinary and DirectAppend uncertainty never replay even after repeated selection", async () =>
        {
            foreach (var directAppend in new[] { false, true })
            {
                await using var f = new Fixture(root, protector);
                f.Adapter.DirectAppend = directAppend; f.Adapter.OriginalTextRead = !directAppend;
                f.Adapter.TitleSource = directAppend ? "ocr" : "uia";
                var run = await f.StartAsync(); f.Workflow.Select(run, 0);
                f.Adapter.ThrowDuringAppend = true;
                await f.Workflow.AppendSelectedAsync(run, default);
                Equal(1, f.Adapter.Appends); True(run.Record.AppendAttemptId is not null, "Uncertain append lost durable intent.");
                Equal("预填结果待确认", run.Record.FillStatus);
                f.Adapter.ThrowDuringAppend = false;
                try { f.Workflow.Select(run, 1); throw new Exception("An uncertain write allowed changing the persisted selected draft."); }
                catch (InvalidOperationException) { }
                await f.Workflow.AppendSelectedAsync(run, default); Equal(1, f.Adapter.Appends);
            }
        });
        yield return ("Third-version workflow retains initial disabled-prefill and adapter write restrictions", async () =>
        {
            foreach (var restriction in new[] { "settings", "adapter", "draft" })
            {
                await using var f = new Fixture(root, protector);
                f.Settings.EnableLiveAppend = restriction != "settings";
                f.Adapter.AllowAppend = restriction != "adapter" && restriction != "draft";
                f.Adapter.OriginalTextRead = restriction != "draft";
                var run = await f.StartAsync(); f.Workflow.Select(run, 0);
                f.Settings.EnableLiveAppend = true; run.Settings.EnableLiveAppend = true; f.Adapter.AllowAppend = true;
                await f.Workflow.AppendSelectedAsync(run, default); Equal(0, f.Adapter.Appends);
                Equal<string?>(null, run.Record.AppendAttemptId);
            }
        });
        yield return ("Third-version workflow OCR DirectAppend uses the frozen frame and protects the current draft at explicit append", async () =>
        {
            await using var f = new Fixture(root, protector);
            f.Adapter.TitleSource = "ocr"; f.Adapter.DirectAppend = true; f.Adapter.OriginalTextRead = false;
            f.Adapter.CanCollectMore = false; f.Adapter.LiveStateAvailable = false;
            var run = await f.StartAsync();
            Equal(3, run.Session!.Candidates.Count); Equal(2, f.Handler.Calls); Equal(0, f.Adapter.UnchangedChecks);
            Equal(false, run.Record.OriginalTextRead); Equal(0, run.Record.OriginalCharacters);
            Equal("", f.Adapter.Baseline!.OriginalText); Equal(0, f.Adapter.Appends);
            // Upstream visual append preserves the draft present at append time, not an unread generation-time baseline.
            f.Adapter.CurrentDraft = " 用户在生成后继续编辑的草稿\r\n "; f.Adapter.LiveStateAvailable = true;
            var currentDraft = f.Adapter.CurrentDraft;
            f.Workflow.Select(run, 1); True(run.CanAppend, "OCR deferred-draft append was incorrectly made copy-only.");
            f.Adapter.BeforeAppend = snapshot =>
            {
                True(ReferenceEquals(f.Adapter.Baseline, snapshot), "Direct append replaced the original adapter capability.");
                Equal("", snapshot.OriginalText); Equal(currentDraft, f.Adapter.CurrentDraft);
                var stored = f.Repository.History("default").Single(x => x.Id == run.Record.Id);
                Equal("写入中", stored.FillStatus); True(stored.AppendAttemptId is not null, "Direct append lacked durable intent.");
            };
            await f.Workflow.AppendSelectedAsync(run, default);
            Equal(1, f.Adapter.Appends); Equal(DraftRules.Append(currentDraft, Greetings[1]), f.Adapter.CurrentDraft);
            Equal("已预填", run.Record.FillStatus); Equal<bool?>(null, run.Record.OriginalPreserved);
            await f.Workflow.AppendSelectedAsync(run, default); Equal(1, f.Adapter.Appends);
        });
        yield return ("Third-version workflow OCR DirectAppend still requires current target and focus before durable intent", async () =>
        {
            foreach (var change in new[] { "identity", "title", "focus" })
            {
                await using var f = new Fixture(root, protector);
                f.Adapter.TitleSource = "ocr"; f.Adapter.DirectAppend = true; f.Adapter.OriginalTextRead = false;
                var run = await f.StartAsync(); f.Workflow.Select(run, 0);
                if (change == "identity") f.Adapter.CurrentIdentity = "other-window";
                else if (change == "title") f.Adapter.Title = "其他联系人";
                else f.Adapter.Ready = false;
                await f.Workflow.AppendSelectedAsync(run, default);
                Equal(0, f.Adapter.Appends); Equal<string?>(null, run.Record.AppendAttemptId);
                True(!run.CanAppend, "A failed current-target check retained append permission.");
            }
        });
        yield return ("Third-version workflow ordinary missing identity stops before capture or model HTTP even with DirectAppend", async () =>
        {
            await using var f = new Fixture(root, protector);
            f.Adapter.TitleSource = "none"; f.Adapter.DisplayNameRead = false;
            f.Adapter.OriginalTextRead = false; f.Adapter.DirectAppend = true;
            var run = await f.StartAsync();
            Equal(0, f.Adapter.Captures); Equal(0, f.Handler.Calls); Equal(0, f.Adapter.Appends);
            Equal<string?>(null, run.Record.AppendAttemptId); True(run.Record.Error.Length > 0, "Missing identity was not rejected visibly.");
        });
        yield return ("Third-version workflow ordinary unread draft without DirectAppend cannot capture or authorize input", async () =>
        {
            await using var f = new Fixture(root, protector); f.Adapter.OriginalTextRead = false;
            var run = await f.StartAsync();
            Equal(0, f.Adapter.Captures); Equal(0, f.Handler.Calls); Equal(0, f.Adapter.Appends);
            Equal<string?>(null, run.Record.AppendAttemptId); True(!run.CanAppend, "Unread ordinary draft granted write permission.");
        });
        yield return ("Third-version workflow DirectAppend frozen first frame does not bypass later upstream collection checks", async () =>
        {
            await using var f = new Fixture(root, protector);
            f.Adapter.TitleSource = "ocr"; f.Adapter.DirectAppend = true; f.Adapter.OriginalTextRead = false;
            f.Adapter.LiveStateAvailable = false;
            var run = await f.StartAsync();
            Equal(1, f.Adapter.Captures); Equal(1, f.Adapter.Scrolls); Equal(1, f.Adapter.UnchangedChecks);
            Equal(0, f.Handler.Calls); Equal<ThirdVersionSession?>(null, run.Session);
            Equal(0, f.Adapter.Appends); Equal<string?>(null, run.Record.AppendAttemptId);
        });
        yield return ("Third-version workflow ForceGeneration strips identity metadata and remains copy-only even with fake identity fields", async () =>
        {
            foreach (var fakeIdentity in new[] { false, true })
            {
                await using var f = new Fixture(root, protector);
                f.Adapter.ForceGeneration = true; f.Adapter.DirectAppend = true;
                f.Adapter.DisplayNameRead = fakeIdentity; f.Adapter.OriginalTextRead = fakeIdentity;
                f.Adapter.TitleSource = "ocr"; f.Adapter.RequiresConversationCheck = true;
                f.Adapter.CaptureLocalTitle = "未经核对的群聊(8)"; f.Handler.ModelTitle = "错误的模型标题";
                f.Repository.SaveContact(new Contact { DisplayName = "周老师", Confirmed = true, AddressTerm = "",
                    IdentityLabel = "强制模式不得读取的本地身份" });
                f.Repository.SaveTask(new TaskRecord { DisplayName = "周老师", Scene = "春节", GenerationStatus = "成功",
                    Greeting = "强制模式不得读取的历史祝福", CompletedAt = DateTimeOffset.UtcNow });
                var run = await f.StartAsync();
                Equal(3, run.Session!.Candidates.Count); Equal(2, f.Handler.Calls); Equal("强制生成", run.Record.DisplayName);
                Equal<Contact?>(null, run.Session.Contact); Equal(0, run.Session.Context.RecentGreetings.Count);
                Equal<string?>(null, run.Record.ContactId); Equal(false, run.Record.ContactReused);
                True(run.Session.Captures.All(x => x.ForceGeneration && !x.RequiresConversationCheck && x.LocalTitle is null),
                    "Force generation retained unverified title or normal identity-check metadata.");
                Equal<string?>(null, run.Record.ObservedDisplayName); Equal<bool?>(null, run.Record.ScreenshotRecipientMatched);
                True(f.Handler.Bodies.All(x => !x.Contains("强制模式不得读取", StringComparison.Ordinal)), "Force generation uploaded saved identity/history.");
                f.Workflow.Select(run, 0); True(!run.CanAppend, "Force generation granted write permission.");
                await f.Workflow.AppendSelectedAsync(run, default);
                Equal(0, f.Adapter.Appends); Equal<string?>(null, run.Record.AppendAttemptId); Equal(3, run.Session.Candidates.Count);
                Equal("", f.Repository.Contacts("default").Single().AddressTerm);
            }
        });
        yield return ("Third-version workflow expiration and release revoke writes and release the retained original adapter", async () =>
        {
            foreach (var expire in new[] { false, true })
            {
                await using var f = new Fixture(root, protector); var run = await f.StartAsync(); f.Workflow.Select(run, 0);
                True(run.ExpiresAt <= f.Clock.GetUtcNow().AddMinutes(10), "Write capability exceeded ten minutes.");
                if (expire) f.Clock.Advance(TimeSpan.FromMinutes(11)); else await f.Workflow.ReleaseAsync(run);
                await f.Workflow.AppendSelectedAsync(run, default); Equal(0, f.Adapter.Appends);
                await f.Workflow.ReleaseAsync(run); True(run.IsReleased, "Released run stayed active.");
                Equal(1, f.Adapter.Restores); Equal(1, f.Adapter.Disposals);
                True(ReferenceEquals(f.Adapter.Baseline, f.Adapter.RestoredSnapshot), "Cleanup reconstructed the baseline.");
            }
        });
        yield return ("Third-version workflow starting a new run revokes the earlier baseline before any new write", async () =>
        {
            await using var f = new Fixture(root, protector); var previous = await f.StartAsync(); f.Workflow.Select(previous, 0);
            var newer = await f.StartAsync(); True(previous.IsReleased, "Starting another run kept the old baseline writable.");
            Equal(1, f.Adapter.Restores); Equal(1, f.Adapter.Disposals);
            await f.Workflow.AppendSelectedAsync(previous, default); Equal(0, f.Adapter.Appends);
            Equal(3, newer.Session!.Candidates.Count); True(!previous.CanAppend, "Old run regained append capability.");
        });
        yield return ("Third-version workflow cancellation preserves candidates and never creates a selected append intent", async () =>
        {
            await using var f = new Fixture(root, protector); var run = await f.StartAsync(); f.Workflow.Select(run, 0);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await f.Workflow.AppendSelectedAsync(run, cancelled.Token);
            Equal(0, f.Adapter.Appends); Equal<string?>(null, run.Record.AppendAttemptId);
            Equal(3, run.Session!.Candidates.Count); Equal(2, run.Record.ApiCallCount);
        });
        yield return ("Third-version workflow counts failed extraction HTTP attempts and restores on cancellation", async () =>
        {
            await using var f = new Fixture(root, protector); f.Handler.CancelExtraction = true;
            var run = await f.StartAsync();
            Equal(1, f.Handler.Calls); Equal(1, run.Record.ApiCallCount); Equal(0, f.Adapter.Appends);
            True(run.Record.Error.Length > 0, "Cancellation was not recorded.");
            await f.Workflow.ReleaseAsync(run); Equal(1, f.Adapter.Restores);
        });
    }

    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
    private static void True(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal LocalRepository Repository { get; }
        internal WorkflowAdapter Adapter { get; } = new();
        internal WorkflowHandler Handler { get; }
        internal MutableClock Clock { get; } = new();
        internal ThirdVersionService Service { get; }
        internal ThirdVersionWorkflow Workflow { get; }
        internal AppSettings Settings { get; }
        private ThirdVersionRun? _run;
        internal Fixture(string root, ISecretProtector protector, int unknownExtractions = 0)
        {
            var folder = Path.Combine(root, "third-workflow-" + Guid.NewGuid().ToString("N"));
            var prompts = Path.Combine(folder, "prompts"); Directory.CreateDirectory(prompts);
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config/third-version"));
            foreach (var name in new[] { "extract.txt", "generate.txt", "revise.txt", "examples.txt" }) File.Copy(Path.Combine(source, name), Path.Combine(prompts, name));
            Repository = new LocalRepository(Path.Combine(folder, "repository"), protector);
            Handler = new WorkflowHandler(Adapter, unknownExtractions); Service = new ThirdVersionService(prompts, Handler);
            Workflow = new ThirdVersionWorkflow(Repository, protector, Service, Clock);
            Settings = new AppSettings { Scene = "春节", Model = "offline-workflow-model", EnableLiveAppend = true,
                BaseUrl = "https://workflow.invalid/v1", AuthorizedOrigin = "https://workflow.invalid", ProtectedApiKey = protector.Protect("offline-not-a-real-key"),
                RequestTimeoutSeconds = 5, TaskTimeoutSeconds = 30 };
        }
        internal async Task<ThirdVersionRun> StartAsync() => _run = await Workflow.StartAsync(Settings, Adapter, default);
        public async ValueTask DisposeAsync() { if (_run is not null) await Workflow.ReleaseAsync(_run); Service.Dispose(); }
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class WorkflowAdapter : IConversationAdapter, IDisposable
    {
        internal int Begins, Captures, Scrolls, Appends, Restores, Disposals, UnchangedChecks;
        internal bool CanCollectMore = true, ScrollSucceeds = true, DuplicateFrames, Ready = true, AllowAppend = true,
            OriginalTextRead = true, DisplayNameRead = true, LiveStateAvailable = true, DirectAppend, ForceGeneration,
            RequiresConversationCheck, ThrowDuringAppend;
        internal string Title = "周老师", TitleSource = "uia", CurrentDraft = " 原有草稿\r\n", CurrentIdentity = "original-window";
        internal string? CaptureLocalTitle;
        internal ConversationSnapshot? Baseline, RestoredSnapshot;
        internal Action<ConversationSnapshot>? BeforeAppend;
        internal bool CancelOnScroll;
        public bool CanAutoAppend => AllowAppend;
        public bool CanCollectMoreContext => CanCollectMore;
        public string AutomationMethod => "workflow-fake";
        public string StateChangeReason => "测试中的会话、焦点或原稿发生变化。";
        public Task<ConversationSnapshot> BeginAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Begins++;
            Baseline = new("original-session", Title, OriginalTextRead ? CurrentDraft : "", CurrentIdentity,
                NativeState: new object(), OriginalTextRead: OriginalTextRead, DisplayNameRead: DisplayNameRead, TitleSource: TitleSource)
                { DirectAppend = DirectAppend, ForceGeneration = ForceGeneration };
            return Task.FromResult(Baseline);
        }
        public Task<Capture> CaptureAsync(ConversationSnapshot snapshot, int index, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); True(ReferenceEquals(Baseline, snapshot), "Capture changed baseline."); Captures++;
            return Task.FromResult(new Capture("capture_" + index, [1, 2, DuplicateFrames ? (byte)3 : (byte)(index + 3)],
                RequiresConversationCheck, CaptureLocalTitle));
        }
        public Task<bool> ScrollUpAsync(ConversationSnapshot snapshot, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Scrolls++;
            if (CancelOnScroll) throw new OperationCanceledException("模拟已有提取结果之后的采集取消。", ct);
            return Task.FromResult(ScrollSucceeds);
        }
        public Task<bool> IsUnchangedAsync(ConversationSnapshot snapshot, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); UnchangedChecks++;
            // Deferred visual append checks the current target; its unread initial draft is not a text baseline.
            return Task.FromResult(LiveStateAvailable && (!snapshot.OriginalTextRead || snapshot.DirectAppend || snapshot.OriginalText == CurrentDraft) &&
                snapshot.WindowIdentity == CurrentIdentity && snapshot.DisplayName == Title);
        }
        public Task<bool> WaitForAppendReadyAsync(ConversationSnapshot snapshot, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Ready); }
        public Task<FillResult> AppendAsync(ConversationSnapshot snapshot, string greeting, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Appends++; BeforeAppend?.Invoke(snapshot);
            if (ThrowDuringAppend) throw new IOException("模拟已发生但无法核实的写入。");
            CurrentDraft = DraftRules.Append(CurrentDraft, greeting);
            return Task.FromResult(new FillResult("已预填", "", snapshot.OriginalTextRead ? true : null));
        }
        public Task RestoreAsync(ConversationSnapshot snapshot) { Restores++; RestoredSnapshot = snapshot; return Task.CompletedTask; }
        public void Dispose() => Disposals++;
    }

    private sealed class WorkflowHandler(WorkflowAdapter adapter, int unknownExtractions) : HttpMessageHandler
    {
        internal int Calls, Extractions, Generations, Revisions;
        internal List<string> Bodies { get; } = [];
        internal Action? BeforeRequest;
        internal bool CancelExtraction, InvalidGeneration;
        internal string? ModelTitle;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Equal("workflow.invalid", request.RequestUri!.Host); Equal("Bearer", request.Headers.Authorization?.Scheme);
            BeforeRequest?.Invoke();
            var text = await request.Content!.ReadAsStringAsync(ct); using var document = JsonDocument.Parse(text);
            Bodies.Add(JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
                { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            var messages = document.RootElement.GetProperty("messages"); Calls++;
            string content;
            if (messages[1].GetProperty("content").ValueKind == JsonValueKind.Array)
            {
                Extractions++;
                if (CancelExtraction) throw new OperationCanceledException("模拟请求取消。", ct);
                var unknown = Extractions <= unknownExtractions;
                content = JsonSerializer.Serialize(new { name = unknown ? null : "周老师", basis = unknown ? "unknown" : "direct",
                    evidence = unknown ? null : "周老师，您好", observed_display_name = adapter.TitleSource == "ocr" && !adapter.ForceGeneration ? null : ModelTitle ?? adapter.Title,
                    evidence_items = unknown ? Array.Empty<object>() : new object[] { new { source = "chat_message", speaker = "self", quote = "周老师，您好", capture_id = "capture_0" } } });
            }
            else if (messages[0].GetProperty("content").GetString()!.Contains("编辑，只负责", StringComparison.Ordinal))
            { Revisions++; content = JsonSerializer.Serialize(new { greeting = Greetings[6], note = "" }); }
            else
            {
                Generations++;
                var user = messages[1].GetProperty("content").GetString()!;
                var neutral = user.Contains("\"neutral\":true", StringComparison.Ordinal);
                content = InvalidGeneration ? "{\"candidates\":[]}" : JsonSerializer.Serialize(new { candidates = Greetings.Skip(Generations == 1 ? 0 : 3).Take(3)
                    .Select(x => neutral ? x.Replace("周老师，", "", StringComparison.Ordinal) : x).ToArray() });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }), Encoding.UTF8, "application/json") };
        }
    }
}
