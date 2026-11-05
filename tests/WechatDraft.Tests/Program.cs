using System.Net;
using System.Text;
using System.Text.Json;
using WechatDraft.Core;
using WechatDraft.Windows;

var failures = 0; var passed = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failures++; }
}
void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}"); }
void True(bool condition, string message) { if (!condition) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException) { return; } throw new Exception("Expected validation exception"); }
if (args.Contains("--native-clipboard"))
{
    await Test("Native clipboard rich text and image round trips", () => { ClipboardNativeChecks.Run(); return Task.CompletedTask; });
    Console.WriteLine($"{passed} passed, {failures} failed");
    return failures == 0 ? 0 : 1;
}
var root = Path.Combine(Path.GetTempPath(), "WechatDraft-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var protector = new DpapiProtector();
var repo = new LocalRepository(root, protector);
var examples = new ExampleService();
var examplePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../config/blessing_examples.json"));
examples.Load(examplePath);
var settings = new AppSettings { Scene = "生日", DemoMode = true };
foreach (var (name, run) in ApiProfileChecks.Cases(root, protector)) await Test(name, run);
foreach (var (name, run) in SentGreetingChecks.Cases(root, protector)) await Test(name, run);
foreach (var (name, run) in ExampleChecks.Cases(examplePath)) await Test(name, run);
LocalRepository ContactRepository() => new(Path.Combine(root, "contacts-" + Guid.NewGuid().ToString("N")), protector);
AppSettings RealSettings() => settings with { DemoMode = false, BaseUrl = "https://test.example/v1", AuthorizedOrigin = "https://test.example", Model = "test-vision", EnableLiveAppend = true };
AddressEvidence ChatAddress(string quote, string captureId = "capture_0") => new() { Source = "chat_message", Speaker = "self", Quote = quote, CaptureId = captureId };

await Test("Vision test images remain complete across desktop DPI contexts", () =>
{
    VisionImageChecks.Run(); return Task.CompletedTask;
});
foreach (var (name, run) in TemplateLocatorChecks.Cases())
    await Test(name, () => { run(); return Task.CompletedTask; });
foreach (var (name, run) in WindowFrameChecks.Cases())
    await Test(name, () => { run(); return Task.CompletedTask; });
foreach (var (name, run) in WindowLayoutChecks.Cases())
    await Test(name, () => { run(); return Task.CompletedTask; });
foreach (var (name, run) in WindowSelectionChecks.Cases())
    await Test(name, () => { run(); return Task.CompletedTask; });
foreach (var (name, run) in ChatTypeChecks.Cases())
    await Test(name, () => { run(); return Task.CompletedTask; });
foreach (var (name, run) in VisionInteractionChecks.Cases())
    await Test(name, () => { run(); return Task.CompletedTask; });
foreach (var (name, run) in UiaForegroundChecks.Cases()) await Test(name, run);
foreach (var (name, run) in DebugSessionChecks.Cases())
    await Test(name, () => { run(); return Task.CompletedTask; });
await Test("Debug logs retain rejected model output and never the bearer credential", async () =>
{
    var folder = Path.Combine(root, "debug-model-" + Guid.NewGuid().ToString("N"));
    const string credential = "secret-must-never-appear-in-artifacts";
    using (var debug = DebugSession.Begin(true, folder, "model-test"))
    {
        using var client = new CompatibleModelClient(new StubHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "rejected-layout-output-marker " + credential } } } }))
        }));
        try { await client.LocateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision", VisionAuthorizedOrigin = "https://test.example" }, credential,
            new("layout", [1, 2, 3]), false, default); throw new Exception("invalid layout accepted"); }
        catch (JsonException) { }
    }
    if (DebugSession.IsSupported)
    {
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        var content = string.Join("\n", files.Select(File.ReadAllText));
        True(content.Contains("rejected-layout-output-marker"), "failed model response was lost");
        True(!content.Contains(credential), "bearer credential leaked into diagnostics");
    }
    else True(!Directory.Exists(folder), "production must not create debug files even when requested");
});
await Test("Debug task records link their artifacts only when capture is permitted", async () =>
{
    var folder = Path.Combine(root, "debug-task-" + Guid.NewGuid().ToString("N"));
    var result = await new TaskCoordinator(repo, examples, protector, folder).RunAsync(settings with { DebugMode = true },
        new FakeAdapter(), new FakeModel(), null, default);
    Equal("成功", result!.GenerationStatus);
    Equal(DebugSession.IsSupported, result.DebugDirectory is not null);
    Equal(DebugSession.IsSupported, Directory.Exists(folder));
});
await Test("Append preserves whitespace and existing newline", () =>
{
    Equal("祝福", DraftRules.Append("", "祝福")); Equal("原文\r\n祝福", DraftRules.Append("原文", "祝福"));
    Equal("原文\n祝福", DraftRules.Append("原文\n", "祝福")); Equal(" \r\n祝福", DraftRules.Append(" ", "祝福"));
    Equal(" 原文 \r\n\r\n祝福", DraftRules.Append(" 原文 \r\n\r\n", "祝福")); return Task.CompletedTask;
});
await Test("Visible character limit counts emoji graphemes", () =>
{
    Equal(1, DraftRules.VisibleLength("👨‍👩‍👧‍👦")); Throws(() => DraftRules.ValidateGreeting(new() { GreetingText = new string('祝', 121) })); return Task.CompletedTask;
});
await Test("Reject placeholders, control characters, unknown relationships", () =>
{
    foreach (var text in new[] { "{称呼}生日快乐", "正文\0", "第一行\n第二行", "```生日快乐```", "" }) Throws(() => DraftRules.ValidateGreeting(new() { GreetingText = text }));
    Throws(() => DraftRules.ValidateGreeting(new() { RelationshipType = "invented", GreetingText = "生日快乐" })); return Task.CompletedTask;
});
await Test("Endpoint prevents credentials leaking through absolute paths", () =>
{
    Equal("https://example.com/v1/chat/completions", DraftRules.ValidateEndpoint(new() { BaseUrl = "https://example.com/v1" }).AbsoluteUri);
    Throws(() => DraftRules.ValidateEndpoint(new() { BaseUrl = "http://example.com" }));
    Throws(() => DraftRules.ValidateEndpoint(new() { BaseUrl = "https://example.com", ApiPath = "https://other.example/steal" }));
    Throws(() => DraftRules.ValidateEndpoint(new() { BaseUrl = "https://name:pass@example.com" }));
    Equal("http", DraftRules.ValidateEndpoint(new() { BaseUrl = "http://localhost:8080/v1", AllowLoopbackHttp = true }).Scheme); return Task.CompletedTask;
});
await Test("DPAPI encrypted SQLite and settings round trip", () =>
{
    var value = "测试联系人-" + Guid.NewGuid().ToString("N"); repo.SaveContact(new() { DisplayName = value });
    True(repo.Contacts("default").Any(x => x.DisplayName == value), "contact roundtrip");
    foreach (var file in Directory.GetFiles(root, "drafts.db*"))
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream(); input.CopyTo(copy);
        True(!Encoding.UTF8.GetString(copy.ToArray()).Contains(value), "plaintext database leak");
    }
    repo.SaveSettings(settings); Equal("生日", repo.LoadSettings().Scene); return Task.CompletedTask;
});
await Test("Legacy duplicate rows remain available without destructive merging", () =>
{
    repo.SaveContact(new() { DisplayName = "同名" }); repo.SaveContact(new() { DisplayName = "同名" });
    Equal(2, repo.Contacts("default").Count(x => x.DisplayName == "同名")); return Task.CompletedTask;
});
await Test("Window preferences round trip while older settings keep the first-launch default", () =>
{
    var layouts = ContactRepository();
    var layout = new WindowLayoutSettings { Left = -1800, Top = 100, WidthDip = 1100, HeightDip = 760, Maximized = false };
    layouts.SaveSettings(settings with { WindowLayout = layout });
    Equal(layout, layouts.LoadSettings().WindowLayout);
    Equal(null, JsonSerializer.Deserialize<AppSettings>("{}", JsonDefaults.Options)!.WindowLayout);
    return Task.CompletedTask;
});
await Test("Name lookup matches remarks and legacy display names exactly", () =>
{
    var contacts = ContactRepository();
    var explicitRemark = new Contact { DisplayName = "自定义显示名", RemarkName = "陈老师" };
    var legacy = new Contact { DisplayName = "旧档案" };
    contacts.SaveContact(explicitRemark); contacts.SaveContact(legacy);
    Equal(explicitRemark.Id, contacts.FindContactByName("default", " 陈老师 ")!.Id);
    Equal(legacy.Id, contacts.FindContactByName("default", "旧档案")!.Id);
    Equal(null, contacts.FindContactByName("default", "陈 老师"));
    Equal(null, contacts.FindContactByName("default", ""));
    contacts.SaveContact(new() { DisplayName = "Alice" });
    Equal(null, contacts.FindContactByName("default", "alice"));
    return Task.CompletedTask;
});
await Test("Name lookup isolates account groups and demonstration contacts", () =>
{
    var contacts = ContactRepository();
    var real = new Contact { AccountScope = "a", DisplayName = "同名" };
    var otherAccount = new Contact { AccountScope = "b", DisplayName = "同名", Confirmed = true };
    var demo = new Contact { AccountScope = "a", DisplayName = "同名", IsDemo = true, Confirmed = true };
    contacts.SaveContact(real); contacts.SaveContact(otherAccount); contacts.SaveContact(demo);
    Equal(real.Id, contacts.FindContactByName("a", "同名")!.Id);
    Equal(otherAccount.Id, contacts.FindContactByName("b", "同名")!.Id);
    Equal(demo.Id, contacts.FindContactByName("a", "同名", isDemo: true)!.Id);
    Equal(null, contacts.FindContactByName("c", "同名"));
    return Task.CompletedTask;
});
await Test("Historical name collisions prefer confirmed records then explicit remarks", () =>
{
    var contacts = ContactRepository();
    var confirmed = new Contact { DisplayName = "同名", AddressTerm = "陈老师", Confirmed = true };
    contacts.SaveContact(confirmed);
    contacts.SaveContact(new() { DisplayName = "其他显示", RemarkName = "同名", AddressTerm = "小陈" });
    Equal(confirmed.Id, contacts.FindContactByName("default", "同名")!.Id);
    var explicitRemark = new Contact { DisplayName = "另外显示", RemarkName = "另一个备注" };
    contacts.SaveContact(explicitRemark);
    contacts.SaveContact(new() { DisplayName = "另一个备注" });
    Equal(explicitRemark.Id, contacts.FindContactByName("default", "另一个备注")!.Id);
    return Task.CompletedTask;
});
await Test("Repeated real tasks reuse the same unconfirmed contact and history reference", async () =>
{
    var contacts = ContactRepository(); var coordinator = new TaskCoordinator(contacts, examples, protector);
    var firstModel = new FakeModel { Response = new() { ObservedDisplayName = "测试联系人", GreetingText = "陈老师，生日快乐，愿您平安顺心！", RelationshipType = "teacher", IdentityLabel = "导师", AddressTerm = "陈老师", AddressEvidence = ChatAddress("陈老师，最近还好吗？"), ContextSufficient = true } };
    var first = await coordinator.RunAsync(RealSettings(), new FakeAdapter { IsDemo = false }, firstModel, null, default);
    var secondModel = new FakeModel { Response = new() { ObservedDisplayName = "测试联系人", GreetingText = "陈老师，生日快乐，愿您平安顺心！", RelationshipType = "friend", IdentityLabel = "朋友", AddressTerm = "小陈", ContextSufficient = true } };
    var second = await coordinator.RunAsync(RealSettings(), new FakeAdapter { IsDemo = false }, secondModel, null, default);
    Equal("成功", first!.GenerationStatus); Equal("已预填", second!.FillStatus);
    Equal(false, first.ContactReused); Equal(true, second.ContactReused);
    Equal(first.ContactId, second.ContactId); Equal(first.ContactId, secondModel.LastContact!.Id);
    Equal("teacher", secondModel.LastContact.Relationship); Equal("陈老师", secondModel.LastContact.AddressTerm);
    Equal("teacher", second.Relationship); Equal("导师", second.IdentityLabel); Equal("陈老师", second.AddressTerm);
    var saved = contacts.Contacts("default").Single();
    Equal("测试联系人", saved.RemarkName); Equal("name_match", saved.BindingStatus); Equal(false, saved.Confirmed);
    Equal("chat_message", saved.AddressTermSource); Equal("陈老师，最近还好吗？", saved.AddressEvidence!.Quote);
    Equal(saved.AddressEvidence, second.AddressEvidence); Equal("chat_message", second.AddressTermSource);
    Equal(2, contacts.History("default").Count); Equal(1, contacts.History("default").Count(x => x.ContactReused));
});
await Test("Equally confirmed name matches reuse the most recently updated profile", async () =>
{
    var contacts = ContactRepository();
    contacts.SaveContact(new() { Id = "a-older", DisplayName = "同名", AddressTerm = "旧称呼" });
    await Task.Delay(25);
    contacts.SaveContact(new() { Id = "z-newer", DisplayName = "同名", AddressTerm = "新称呼" });
    Equal("z-newer", contacts.FindContactByName("default", "同名")!.Id);
    Equal(2, contacts.Contacts("default").Count);
});
await Test("Legacy unconfirmed profiles fill missing fields without overwriting saved data", async () =>
{
    var contacts = ContactRepository(); var saved = new Contact { DisplayName = "测试联系人", IdentityLabel = "前同事" }; contacts.SaveContact(saved);
    var model = new FakeModel { Response = new() { ObservedDisplayName = "测试联系人", GreetingText = "张哥，生日快乐，愿您平安顺心！", RelationshipType = "colleague", IdentityLabel = "新推断", AddressTerm = "张哥", AddressEvidence = ChatAddress("张哥，周末有空吗？"), ContextSufficient = true } };
    var result = await new TaskCoordinator(contacts, examples, protector).RunAsync(RealSettings(), new FakeAdapter { IsDemo = false }, model, null, default);
    Equal(saved.Id, result!.ContactId); Equal(true, result.ContactReused);
    var updated = contacts.Contacts("default").Single();
    Equal("colleague", updated.Relationship); Equal("前同事", updated.IdentityLabel); Equal("张哥", updated.AddressTerm); Equal("测试联系人", updated.RemarkName);
});
await Test("Confirmed empty contact fields stay unchanged when the model guesses", async () =>
{
    var contacts = ContactRepository(); var saved = new Contact { DisplayName = "旧显示名", RemarkName = "测试联系人", Confirmed = true }; contacts.SaveContact(saved);
    var model = new FakeModel { Response = new() { ObservedDisplayName = "测试联系人", GreetingText = "生日快乐，愿您平安顺心！", RelationshipType = "teacher", IdentityLabel = "老师", AddressTerm = "陈老师", ContextSufficient = true } };
    var result = await new TaskCoordinator(contacts, examples, protector).RunAsync(RealSettings(), new FakeAdapter { IsDemo = false }, model, null, default);
    Equal(saved.Id, result!.ContactId); Equal("旧显示名", model.LastContact!.DisplayName);
    Equal("unknown", result.Relationship); Equal(null, result.IdentityLabel); Equal(null, result.AddressTerm);
    Equal(true, contacts.Contacts("default").Single().Confirmed);
});
await Test("Screenshot mismatch does not enrich or duplicate an existing contact", async () =>
{
    var contacts = ContactRepository(); var saved = new Contact { DisplayName = "测试联系人" }; contacts.SaveContact(saved);
    var adapter = new FakeAdapter { IsDemo = false };
    var model = new FakeModel { Response = new() { ObservedDisplayName = "其他聊天", GreetingText = "陈老师，生日快乐，愿您平安顺心！", RelationshipType = "teacher", AddressTerm = "陈老师", AddressEvidence = ChatAddress("陈老师，您好！") } };
    var result = await new TaskCoordinator(contacts, examples, protector).RunAsync(RealSettings(), adapter, model, null, default);
    Equal(false, result!.ScreenshotRecipientMatched); Equal(0, adapter.Appends);
    var unchanged = contacts.Contacts("default").Single(); Equal(saved.Id, unchanged.Id); Equal("unknown", unchanged.Relationship); Equal(null, unchanged.AddressTerm);
});
await Test("Account scopes remain isolated", () =>
{
    repo.SaveContact(new() { AccountScope = "account-b", DisplayName = "B" });
    True(repo.Contacts("default").All(x => x.DisplayName != "B"), "account data mixed"); return Task.CompletedTask;
});
await Test("Example reload retains last valid version", () =>
{
    var version = examples.Version; var file = Path.Combine(root, "bad.json"); File.WriteAllText(file, "broken");
    Equal(false, examples.TryReload(file)); Equal(version, examples.Version); return Task.CompletedTask;
});
await Test("Four capture maximum, three scroll maximum", async () =>
{
    var adapter = new FakeAdapter(); var model = new FakeModel { NeedsMore = true }; var coordinator = new TaskCoordinator(repo, examples, protector);
    var result = await coordinator.RunAsync(settings, adapter, model, null, default);
    Equal(4, result!.CaptureCount); Equal(3, result.ScrollCount); Equal(1, adapter.Appends); Equal(4, model.Calls);
});
await Test("Input inspection failure ends progress and never calls the model", async () =>
{
    var coordinator = new TaskCoordinator(repo, examples, protector);
    TaskProgress? last = null; coordinator.Progress += p => last = p;
    var model = new FakeModel(); var adapter = new FakeAdapter { BeginError = new InvalidOperationException("input unavailable") };
    var result = await coordinator.RunAsync(settings, adapter, model, null, default);
    Equal("失败", result!.GenerationStatus); Equal("未预填", result.FillStatus); Equal(0, model.Calls); Equal(0, adapter.Appends);
    Equal("检查失败", last!.Stage); Equal("input unavailable", last.Detail); Equal(100, last.Percent); Equal(false, coordinator.IsRunning);
    var retry = await coordinator.RunAsync(settings, new FakeAdapter(), new FakeModel(), null, default);
    Equal("成功", retry!.GenerationStatus); Equal("处理完成", last.Stage);
});
await Test("Cancelling input inspection publishes a completed cancelled state", async () =>
{
    var coordinator = new TaskCoordinator(repo, examples, protector);
    TaskProgress? last = null; coordinator.Progress += p => last = p;
    using var cancelled = new CancellationTokenSource();
    var model = new FakeModel(); var adapter = new FakeAdapter { BeginDelayMs = 10000 };
    var pending = coordinator.RunAsync(settings, adapter, model, null, cancelled.Token);
    cancelled.Cancel(); var result = await pending;
    Equal("取消", result!.GenerationStatus); Equal(0, model.Calls); Equal(0, adapter.Appends);
    Equal("已取消", last!.Stage); Equal(100, last.Percent); Equal(false, coordinator.IsRunning);
});
await Test("Waiting to return to WeChat keeps the greeting durable but creates no write intent", async () =>
{
    var taskRepository = ContactRepository();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var adapter = new FakeAdapter { IsDemo = false, WaitForAppend = ct => { entered.SetResult(); return ready.Task.WaitAsync(ct); } };
    var pending = new TaskCoordinator(taskRepository, examples, protector).RunAsync(RealSettings(), adapter,
        new FakeModel { ObservedName = "测试联系人" }, null, default);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var stored = taskRepository.History("default", 10).Single();
    Equal("成功", stored.GenerationStatus); True(stored.Greeting.Length > 0, "generated greeting was not saved");
    Equal(null, stored.AppendAttemptId); Equal(0, adapter.Appends);
    ready.SetResult(true);
    var result = await pending;
    Equal(1, adapter.Appends); Equal("已预填", result!.FillStatus); True(result.AppendAttemptId is not null, "write intent missing");
});
await Test("A changed chat after returning from the assistant stops before creating write intent", async () =>
{
    var adapter = new FakeAdapter { IsDemo = false, StateChangeReason = "微信聊天或聊天控件已变化，已停止预填。" };
    adapter.WaitForAppend = _ => { adapter.Valid = false; return Task.FromResult(true); };
    using var fallback = new FallbackConversationAdapter(adapter, () => throw new Exception("must not fall back"));
    var result = await new TaskCoordinator(ContactRepository(), examples, protector).RunAsync(RealSettings(), fallback,
        new FakeModel { ObservedName = "测试联系人" }, null, default);
    Equal("成功", result!.GenerationStatus); Equal(adapter.StateChangeReason, result.Error);
    Equal(null, result.AppendAttemptId); Equal(0, adapter.Appends);
});
await Test("Cancelling while waiting for WeChat preserves generation and never creates uncertain input", async () =>
{
    using var cancellation = new CancellationTokenSource();
    var adapter = new FakeAdapter { WaitForAppend = ct => { cancellation.Cancel(); return Task.FromCanceled<bool>(ct); } };
    var result = await new TaskCoordinator(ContactRepository(), examples, protector).RunAsync(settings, adapter,
        new FakeModel(), null, cancellation.Token);
    Equal("成功", result!.GenerationStatus); Equal("待手动处理", result.FillStatus);
    Equal(null, result.AppendAttemptId); Equal(0, adapter.Appends); True(result.Greeting.Length > 0, "greeting lost on cancellation");
});
await Test("Readiness timeout returns its exact reason without a write attempt", async () =>
{
    var adapter = new FakeAdapter { StateChangeReason = "等待返回微信超过 30 秒，未预填。", WaitForAppend = _ => Task.FromResult(false) };
    var result = await new TaskCoordinator(ContactRepository(), examples, protector).RunAsync(settings, adapter, new FakeModel(), null, default);
    Equal("成功", result!.GenerationStatus); Equal(adapter.StateChangeReason, result.Error);
    Equal(null, result.AppendAttemptId); Equal(0, adapter.Appends);
});
await Test("User modification prevents input even with successful generation", async () =>
{
    var adapter = new FakeAdapter(); var model = new FakeModel { BeforeReturn = () => adapter.Valid = false };
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, model, null, default);
    Equal("成功", result!.GenerationStatus); Equal("待手动处理", result.FillStatus); Equal(0, adapter.Appends);
});
await Test("Disabled append generates without input intent", async () =>
{
    var adapter = new FakeAdapter { CanAutoAppend = false };
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, new FakeModel(), null, default);
    Equal("待手动处理", result!.FillStatus); Equal(null, result.AppendAttemptId); Equal(0, adapter.Appends);
});
await Test("A real task with prefill disabled never writes even if its adapter can append", async () =>
{
    var adapter = new FakeAdapter { IsDemo = false };
    var result = await new TaskCoordinator(ContactRepository(), examples, protector).RunAsync(RealSettings() with { EnableLiveAppend = false },
        adapter, new FakeModel { ObservedName = "测试联系人" }, null, default);
    Equal("成功", result!.GenerationStatus); Equal(0, adapter.Appends); Equal(null, result.AppendAttemptId);
    True(result.Error.Contains("预填已关闭"), "disabled preference was ignored");
});
await Test("Changing the preference after a task starts cannot grant that task input permission", async () =>
{
    var livePreferences = RealSettings() with { EnableLiveAppend = false };
    var snapshotSettings = livePreferences with { }; var adapter = new FakeAdapter { IsDemo = false };
    var model = new FakeModel { ObservedName = "测试联系人", BeforeReturn = () => livePreferences.EnableLiveAppend = true };
    var result = await new TaskCoordinator(ContactRepository(), examples, protector).RunAsync(snapshotSettings, adapter, model, null, default);
    Equal("成功", result!.GenerationStatus); Equal(0, adapter.Appends); Equal(null, result.AppendAttemptId);
});
await Test("Editing live settings from another page does not change an active task", async () =>
{
    var live = RealSettings() with { Model = "model-before", Scene = "生日", ExtraInstruction = "原要求", AccountScope = "before" };
    var adapter = new FakeAdapter { IsDemo = false, OnBegin = () =>
    {
        live.Model = "model-after"; live.Scene = "元旦"; live.ExtraInstruction = "下次要求";
        live.AccountScope = "after"; live.EnableLiveAppend = false;
    } };
    var model = new FakeModel { ObservedName = "测试联系人" };
    var result = await new TaskCoordinator(ContactRepository(), examples, protector).RunAsync(live, adapter, model, null, default);
    Equal("成功", result!.GenerationStatus); Equal("model-before", result.Model); Equal("before", result.AccountScope);
    Equal("生日", result.Scene); Equal("原要求", model.LastSettings!.ExtraInstruction); Equal(true, model.LastSettings.EnableLiveAppend);
    Equal(1, adapter.Appends); Equal("model-after", live.Model);
});
await Test("Editing examples while a conversation is inspected preserves the task's starting library", async () =>
{
    var source = new ExampleService(); source.Load(examplePath);
    var version = source.Version; var expected = source.Select("生日", "unknown").Select(item => item.Id).ToArray();
    var target = Path.Combine(root, "next-page-examples.json");
    var adapter = new FakeAdapter { OnBegin = () => source.Save(target, source.Library with { Examples = [] }) };
    var model = new FakeModel();
    var result = await new TaskCoordinator(ContactRepository(), source, protector).RunAsync(settings, adapter, model, null, default);
    Equal("成功", result!.GenerationStatus); Equal(version, result.ExamplesVersion);
    True(expected.SequenceEqual(result.ExampleIds) && expected.SequenceEqual(model.LastExamples.Select(item => item.Id)), "active task used the later library");
    Equal(0, source.Library.Examples.Count);
    var next = await new TaskCoordinator(ContactRepository(), source, protector).RunAsync(settings, new FakeAdapter(), new FakeModel(), null, default);
    Equal(source.Version, next!.ExamplesVersion); Equal(0, next.ExampleIds.Length);
});
await Test("An unverifiable conversation keeps the greeting without creating input intent", async () =>
{
    var adapter = new FakeAdapter { IsDemo = false, CanAutoAppend = false, AutoAppendUnavailableReason = "缺少可复查的会话选择标识。" };
    var result = await new TaskCoordinator(ContactRepository(), examples, protector).RunAsync(RealSettings(), adapter,
        new FakeModel { ObservedName = "测试联系人" }, null, default);
    Equal("成功", result!.GenerationStatus); Equal(0, adapter.Appends); Equal(null, result.AppendAttemptId);
    True(result.Greeting.Length > 0 && result.Error.Contains("会话选择标识"), "manual fallback lost the result or reason");
});
await Test("Identical displayed titles cannot authorize input after the conversation changes", async () =>
{
    var adapter = new FakeAdapter { IsDemo = false };
    var model = new FakeModel { ObservedName = "测试联系人", BeforeReturn = () => adapter.Valid = false };
    var result = await new TaskCoordinator(ContactRepository(), examples, protector).RunAsync(RealSettings(), adapter, model, null, default);
    Equal(true, result!.ScreenshotRecipientMatched); Equal("成功", result.GenerationStatus); Equal(0, adapter.Appends);
    Equal(null, result.AppendAttemptId); Equal("待手动处理", result.FillStatus);
});
await Test("UIA runtime identities distinguish same-name conversations and replaced controls", () =>
{
    var identity = new UiaConversationIdentity("header-1", "editor-1", "session-A");
    True(identity.Matches(identity with { }), "unchanged binding rejected");
    True(!identity.Matches(identity with { SelectedSession = "session-B" }), "same-title peer change accepted");
    True(!identity.Matches(identity with { Header = "header-2" }), "replaced header accepted");
    True(!identity.Matches(identity with { Editor = "editor-2" }), "replaced input accepted");
    True(!identity.Matches(null) && !identity.Matches(identity with { SelectedSession = "" }), "unknown peer accepted");
    return Task.CompletedTask;
});
await Test("The UIA append entry refuses input without a verified conversation snapshot", async () =>
{
    using var adapter = new WechatAdapter(0, true);
    var result = await adapter.AppendAsync(new("s", "同名联系人", "原文", "w", false), "祝福", default);
    Equal("待手动处理", result.Status); True(result.Message.Contains("会话"), "missing identity reason");
});
await Test("A live chat view remains verifiable when its selected list item is virtualized", () =>
{
    var identity = new UiaConversationIdentity("header-A", "editor", "session-A", "page/content-A");
    True(identity.Matches(identity with { SelectedSession = "" }), "hidden list item stopped the unchanged chat view");
    True(identity.Matches(identity with { }), "visible unchanged selection rejected");
    Equal("chat-view", identity.BindingMethod); return Task.CompletedTask;
});
await Test("A task can bind the open chat view while the left list already hides its selection", () =>
{
    var identity = new UiaConversationIdentity("header-A", "editor", "", "page/content-A");
    True(identity.Matches(identity with { }), "initially hidden selection rejected");
    True(identity.Matches(identity with { SelectedSession = "session-A" }), "reappearing selection rejected");
    True(!identity.Matches(identity with { ChatView = "page/content-B" }), "same-name replacement view accepted");
    True(!identity.Matches(identity with { Header = "header-B" }), "replacement header accepted");
    True(!identity.Matches(identity with { Editor = "editor-B" }), "replacement input accepted");
    return Task.CompletedTask;
});
await Test("A chat view binding never downgrades to a matching title or selected item", () =>
{
    var identity = new UiaConversationIdentity("header", "editor", "session-A", "page/content-A");
    True(!identity.Matches(identity with { ChatView = "" }), "missing chat view accepted by selection alone");
    True(!identity.Matches(identity with { ChatView = "page/content-B" }), "replacement chat accepted by selection alone");
    True(!identity.Matches(identity with { SelectedSession = "session-B" }), "contradictory visible selection accepted");
    return Task.CompletedTask;
});
await Test("Layouts without a verified chat view still require an unchanged selected item", () =>
{
    var identity = new UiaConversationIdentity("header", "editor", "session-A");
    Equal("session-selection", identity.BindingMethod);
    True(!identity.Matches(identity with { SelectedSession = "", ChatView = "new-view" }), "task changed its identity anchor midflight");
    var titleOnly = identity with { SelectedSession = "" };
    True(!titleOnly.Matches(titleOnly), "title and input alone accepted as a conversation identity");
    return Task.CompletedTask;
});
await Test("A same-title replacement chat view keeps the generated greeting without writing", async () =>
{
    var identity = new UiaConversationIdentity("header", "editor", "", "page/content-A");
    var adapter = new FakeAdapter { IsDemo = false };
    var model = new FakeModel { ObservedName = "测试联系人", BeforeReturn = () =>
        adapter.Valid = identity.Matches(identity with { ChatView = "page/content-B" }) };
    var result = await new TaskCoordinator(ContactRepository(), examples, protector).RunAsync(RealSettings(), adapter, model, null, default);
    Equal("成功", result!.GenerationStatus); Equal(true, result.ScreenshotRecipientMatched);
    Equal(0, adapter.Appends); Equal(null, result.AppendAttemptId); True(result.Greeting.Length > 0, "greeting lost");
});
await Test("Prefill is off in new profiles and survives a settings round trip when explicitly enabled", () =>
{
    Equal(false, new AppSettings().EnableLiveAppend);
    var preferences = ContactRepository(); preferences.SaveSettings(settings with { EnableLiveAppend = true });
    Equal(true, preferences.LoadSettings().EnableLiveAppend); return Task.CompletedTask;
});
await Test("Screenshot-only generation records unread drafts without claiming an empty original", async () =>
{
    var adapter = new FakeAdapter { CanAutoAppend = false, OriginalTextRead = false };
    var coordinator = new TaskCoordinator(repo, examples, protector); var progress = new List<TaskProgress>();
    coordinator.Progress += progress.Add;
    var result = await coordinator.RunAsync(settings, adapter, new FakeModel(), null, default);
    Equal("成功", result!.GenerationStatus); Equal(false, result.OriginalTextRead); Equal(null, result.OriginalPreserved);
    Equal(null, result.AppendAttemptId); Equal(0, adapter.Appends);
    True(progress.Any(stage => stage.Detail.Contains("未读取草稿")) && !progress.Any(stage => stage.Detail.Contains("保留原有 0")), "unread draft must not be reported as empty");
});
await Test("An unread original draft can never authorize automatic input", async () =>
{
    var adapter = new FakeAdapter { OriginalTextRead = false }; var model = new FakeModel();
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, model, null, default);
    Equal("失败", result!.GenerationStatus); Equal(0, model.Calls); Equal(0, adapter.Appends); Equal(null, result.AppendAttemptId);
});
await Test("A frozen context is the final model round and never scrolls for more history", async () =>
{
    var adapter = new FakeAdapter { CanAutoAppend = false, OriginalTextRead = false, CanCollectMoreContext = false };
    var model = new FakeModel { NeedsMore = true };
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, model, null, default);
    Equal("成功", result!.GenerationStatus); Equal(1, model.Calls); Equal(true, model.LastFinalRound);
    Equal(1, result.CaptureCount); Equal(0, adapter.Scrolls); Equal(0, adapter.Appends);
});
await Test("Enabling append after screenshot-only generation cannot promote an unread draft", async () =>
{
    var adapter = new FakeAdapter { CanAutoAppend = false, OriginalTextRead = false };
    var model = new FakeModel { BeforeReturn = () => adapter.CanAutoAppend = true };
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, model, null, default);
    Equal("成功", result!.GenerationStatus); Equal("待手动处理", result.FillStatus); Equal(0, adapter.Appends); Equal(null, result.AppendAttemptId);
    using var client = new CompatibleModelClient(new StubHandler(_ => throw new Exception("must not call")));
    using var visual = new WechatVisionAdapter(0, 0, true, settings, client, protector);
    var rejected = await visual.AppendAsync(new("s", "联系人", "", "w", false, OriginalTextRead: false), "祝福", default);
    Equal("待手动处理", rejected.Status);
});
await Test("Uncertain input result is never replayed", async () =>
{
    var adapter = new FakeAdapter { Result = new("预填结果待确认", "test") };
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, new FakeModel(), null, default);
    Equal(1, adapter.Appends); Equal("预填结果待确认", result!.FillStatus);
    True(result.AppendAttemptId is not null, "missing durable input intent");
});
await Test("Cancel ignores late model result", async () =>
{
    using var cancellation = new CancellationTokenSource(); var adapter = new FakeAdapter();
    var model = new FakeModel { BeforeReturn = cancellation.Cancel };
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, model, null, cancellation.Token);
    Equal(0, adapter.Appends); True(result!.Error.Contains("取消"), "missing cancelled status");
});
await Test("Rapid trigger never starts a second task", async () =>
{
    var coordinator = new TaskCoordinator(repo, examples, protector); var model = new FakeModel { DelayMs = 100 };
    var first = coordinator.RunAsync(settings, new FakeAdapter(), model, null, default);
    var second = await coordinator.RunAsync(settings, new FakeAdapter(), model, null, default);
    Equal(null, second); await first; Equal(1, model.Calls);
});
await Test("Persistent invalid model format only regenerates once", async () =>
{
    var model = new FakeModel { Invalid = true }; var adapter = new FakeAdapter();
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, model, null, default);
    Equal(2, model.Calls); Equal(0, adapter.Appends); Equal("失败", result!.GenerationStatus);
});
await Test("Recovery marks issued input as uncertain without replay", () =>
{
    var task = new TaskRecord { FillStatus = "写入中", AppendAttemptId = "issued" }; repo.SaveTask(task); repo.RecoverInterrupted();
    Equal("预填结果待确认", repo.History(null).Single(x => x.Id == task.Id).FillStatus); return Task.CompletedTask;
});
await Test("HTTP redirect does not forward bearer credentials", async () =>
{
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.example/") } });
    using var client = new CompatibleModelClient(handler);
    try { await client.GenerateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision" }, "secret", [new("0", [1])], null, [], true, default); throw new Exception("expected rejection"); }
    catch (InvalidDataException) { Equal(1, handler.Calls); }
});
await Test("GLM vision uses data-only function output and preserves bearer auth", async () =>
{
    var modelData = JsonSerializer.Serialize(new ModelResponse { GreetingText = "新春快乐，愿新的一年平安顺心！", ObservedDisplayName = "测试联系人" }, JsonDefaults.Options);
    var handler = new StubHandler(request =>
    {
        Equal("Bearer", request.Headers.Authorization!.Scheme); Equal("test-key", request.Headers.Authorization.Parameter);
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Equal("submit_draft", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        var prompt = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        True(prompt.Contains("春节") && !prompt.Contains("\\u6625"), "Chinese scene must remain readable inside prompt JSON");
        True(!body.RootElement.TryGetProperty("response_format", out _), "vision must not use unsupported JSON mode");
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "tool_calls", message = new { tool_calls = new[] { new { function = new { name = "submit_draft", arguments = modelData } } } } } } })) };
    });
    using var client = new CompatibleModelClient(handler);
    var result = await client.GenerateAsync(new() { BaseUrl = "https://open.bigmodel.cn/api/paas/v4", Model = "GLM-4.6V-Flash" }, "test-key", [new("0", [1])], null, [], true, default);
    Equal("测试联系人", result.ObservedDisplayName); Equal(1, handler.Calls);
});
await Test("Unknown function and truncated responses cannot become drafts", async () =>
{
    foreach (var truncated in new[] { false, true })
    {
        var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = truncated ? "length" : "tool_calls", message = new { content = "{}", tool_calls = new[] { new { function = new { name = "send_message", arguments = "{}" } } } } } } })) });
        using var client = new CompatibleModelClient(handler);
        try { await client.GenerateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision" }, "", [new("0", [1])], null, [], true, default); throw new Exception("unsafe result accepted"); }
        catch (InvalidDataException) { Equal(1, handler.Calls); }
    }
});
await Test("Volcengine image check uses subscription route and disabled thinking", async () =>
{
    var handler = new StubHandler(request =>
    {
        Equal("https://ark.cn-beijing.volces.com/api/plan/v3/chat/completions", request.RequestUri!.AbsoluteUri);
        Equal("Bearer", request.Headers.Authorization!.Scheme);
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        True(!body.RootElement.TryGetProperty("tools", out _), "GLM-only tools must not leak into Ark requests");
        True(body.RootElement.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString()!.StartsWith("data:image/png;base64,"), "image missing");
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{\\\"code\\\":\\\"W1234\\\"}\"}}]}") };
    });
    using var client = new CompatibleModelClient(handler);
    await client.TestVisionAsync(new() { BaseUrl = "https://ark.cn-beijing.volces.com/api/plan/v3", Model = "doubao-seed-2.1-lite" }, "test-key", [1], "W1234", default);
    Equal(1, handler.Calls);
});
await Test("Generic vision providers retain standard JSON text protocol", async () =>
{
    var handler = new StubHandler(request =>
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        True(!body.RootElement.TryGetProperty("thinking", out _) && !body.RootElement.TryGetProperty("tools", out _), "provider options leaked");
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new ModelResponse { GreetingText = "生日快乐，愿你平安顺心！" }, JsonDefaults.Options) } } } })) };
    });
    using var client = new CompatibleModelClient(handler);
    var result = await client.GenerateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision", Scene = "生日" }, "", [new("0", [1])], null, [], true, default);
    Equal("生日快乐，愿你平安顺心！", result.GreetingText);
});
await Test("Legacy tone preferences do not constrain new requests and custom style requirements remain available", async () =>
{
    var legacySettings = JsonSerializer.Deserialize<AppSettings>("""
        {"baseUrl":"https://test.example/v1","model":"vision","scene":"生日","tone":"formal","extraInstruction":"亲切一点"}
        """, JsonDefaults.Options)!;
    var handler = new StubHandler(request =>
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        using var prompt = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
        True(!prompt.RootElement.TryGetProperty("tone", out _), "removed tone preference still affects the request");
        Equal("亲切一点", prompt.RootElement.GetProperty("extra_requirement").GetString());
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new ModelResponse { GreetingText = "生日快乐，愿你平安顺心！" }, JsonDefaults.Options) } } } })) };
    });
    using var client = new CompatibleModelClient(handler);
    await client.GenerateAsync(legacySettings, "", [new("0", [1])], null, [], true, default);
    True(!JsonSerializer.Serialize(legacySettings, JsonDefaults.Options).Contains("\"tone\""), "legacy preference must not return after saving");
});
await Test("Emoji requirements come only from extra instructions and ignore the removed checkbox", async () =>
{
    foreach (var legacyAllowed in new[] { false, true })
    foreach (var useFunction in new[] { false, true })
    foreach (var instruction in new[] { "加 3 个 Emoji", "不要使用表情", "" })
    {
        var legacySettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new
        {
            baseUrl = useFunction ? "https://open.bigmodel.cn/api/paas/v4" : "https://test.example/v1",
            model = useFunction ? "glm-4.6v-flash" : "vision", scene = "生日", allowEmoji = legacyAllowed, extraInstruction = instruction
        }), JsonDefaults.Options)!;
        var greeting = "生日快乐，愿你平安顺心！" + (instruction == "加 3 个 Emoji" ? "🎂🎉✨" : "");
        using var client = new CompatibleModelClient(new StubHandler(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var messages = body.RootElement.GetProperty("messages");
            using var prompt = JsonDocument.Parse(messages[1].GetProperty("content")[0].GetProperty("text").GetString()!);
            Equal(instruction, prompt.RootElement.GetProperty("extra_requirement").GetString());
            True(!prompt.RootElement.TryGetProperty("allow_emoji", out _) && !prompt.RootElement.TryGetProperty("emoji_policy", out _), "removed checkbox still constrains generation");
            True(messages[0].GetProperty("content").GetString()!.Contains("是否使用Emoji及其数量遵从用户本次的extra_requirement"), "custom emoji requirement is not specified");
            var content = JsonSerializer.Serialize(new ModelResponse { GreetingText = greeting }, JsonDefaults.Options);
            object message = useFunction
                ? new { tool_calls = new[] { new { function = new { name = "submit_draft", arguments = content } } } }
                : new { content };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message } } })) };
        }));
        var result = await client.GenerateAsync(legacySettings, "", [new("0", [1])], null, [], true, default);
        Equal(greeting, result.GreetingText);
        True(!JsonSerializer.Serialize(legacySettings, JsonDefaults.Options).Contains("\"allowEmoji\""), "removed checkbox must not return after saving");
    }
});
await Test("Unconfirmed stored identity and address are included in the model request", async () =>
{
    var handler = new StubHandler(request =>
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        using var prompt = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
        var stored = prompt.RootElement.GetProperty("stored_contact");
        Equal("陈老师", stored.GetProperty("addressTerm").GetString());
        Equal("导师", stored.GetProperty("identityLabel").GetString()); Equal(false, stored.GetProperty("confirmed").GetBoolean());
        Equal(JsonValueKind.Null, prompt.RootElement.GetProperty("confirmed_contact").ValueKind);
        var salutation = prompt.RootElement.GetProperty("salutation");
        Equal("陈老师", salutation.GetProperty("required_address_term").GetString());
        Equal(true, salutation.GetProperty("include_when_known").GetBoolean());
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new ModelResponse { GreetingText = "陈老师，生日快乐，愿您平安顺心！" }, JsonDefaults.Options) } } } })) };
    });
    using var client = new CompatibleModelClient(handler);
    await client.GenerateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision", Scene = "生日" }, "", [new("0", [1])],
        new Contact { DisplayName = "测试联系人", Relationship = "teacher", IdentityLabel = "导师", AddressTerm = "陈老师", AddressTermSource = "user" }, [], true, default);
    Equal(1, handler.Calls);
});
await Test("Known salutations belong in the greeting itself and saved values take precedence", () =>
{
    var contact = new Contact { AddressTerm = " 陈老师 ", AddressTermSource = "user" };
    Capture[] captures = [new("capture_0", [1])];
    var answer = new ModelResponse { AddressTerm = "小陈", GreetingText = "陈老师，生日快乐！" };
    DraftRules.ValidateSalutation(answer, contact, captures);
    foreach (var text in new[] { "生日快乐！", "小陈，生日快乐！", "生日快乐，陈老师！" })
        Throws(() => DraftRules.ValidateSalutation(answer with { GreetingText = text }, contact, captures));
    DraftRules.ValidateSalutation(new() { AddressTerm = "小王", AddressEvidence = ChatAddress("小王，最近忙吗？"), GreetingText = "小王，新春快乐！" }, null, captures);
    Throws(() => DraftRules.ValidateSalutation(new() { AddressTerm = "小王", AddressEvidence = ChatAddress("小王，最近忙吗？"), GreetingText = "新春快乐！" }, null, captures));
    DraftRules.ValidateSalutation(new() { GreetingText = "生日快乐！" }, null, captures);
    DraftRules.ValidateSalutation(new() { AddressTerm = "陈老师", GreetingText = "生日快乐！" }, new() { Confirmed = true }, captures);
    return Task.CompletedTask;
});
await Test("A missing salutation regenerates once and only the corrected greeting is appended", async () =>
{
    var contacts = ContactRepository(); contacts.SaveContact(new() { DisplayName = "测试联系人", AddressTerm = "陈老师", AddressTermSource = "user" });
    var adapter = new FakeAdapter { IsDemo = false }; var model = new FakeModel();
    model.BeforeReturn = () => model.Response = new() { ObservedDisplayName = "测试联系人", ContextSufficient = true,
        GreetingText = model.Calls == 1 ? "生日快乐，愿您平安顺心！" : "陈老师，生日快乐，愿您平安顺心！" };
    var result = await new TaskCoordinator(contacts, examples, protector).RunAsync(RealSettings(), adapter, model, null, default);
    Equal(2, model.Calls); Equal(1, adapter.Appends); Equal("已预填", result!.FillStatus);
    True(result.Greeting.StartsWith("陈老师"), "uncorrected greeting was used");
});
await Test("Repeatedly missing or conflicting salutations never write or change the saved address", async () =>
{
    foreach (var text in new[] { "生日快乐，愿您平安顺心！", "王老师，生日快乐，愿您平安顺心！" })
    {
        var contacts = ContactRepository(); contacts.SaveContact(new() { DisplayName = "测试联系人", AddressTerm = "陈老师", Confirmed = true });
        var adapter = new FakeAdapter { IsDemo = false };
        var model = new FakeModel { Response = new() { GreetingText = text, AddressTerm = "王老师", ObservedDisplayName = "测试联系人" } };
        var result = await new TaskCoordinator(contacts, examples, protector).RunAsync(RealSettings(), adapter, model, null, default);
        Equal(2, model.Calls); Equal(0, adapter.Appends); Equal("失败", result!.GenerationStatus);
        Equal(null, result.AppendAttemptId); Equal("陈老师", contacts.Contacts("default").Single().AddressTerm);
    }
});
await Test("The model client rejects a salutation present only in metadata", async () =>
{
    using var client = new CompatibleModelClient(new StubHandler(_ => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new ModelResponse
        { AddressTerm = "小王", AddressEvidence = ChatAddress("小王，最近忙吗？", "0"), GreetingText = "生日快乐，愿你平安顺心！" }, JsonDefaults.Options) } } } })) }));
    try
    {
        await client.GenerateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision", Scene = "生日" }, "", [new("0", [1])], null, [], true, default);
        throw new Exception("metadata-only salutation was accepted");
    }
    catch (InvalidDataException e) { True(e.Message.Contains("称呼"), "wrong rejection"); }
});
await Test("New salutations require a self-sent chat excerpt from a provided capture", () =>
{
    Capture[] captures = [new("capture_0", [1])];
    var evidence = ChatAddress("陈老师，最近还好吗？");
    var answer = new ModelResponse { AddressTerm = "陈老师", AddressEvidence = evidence, GreetingText = "陈老师，生日快乐！" };
    DraftRules.ValidateSalutation(answer, null, captures);
    foreach (var invalid in new AddressEvidence?[] { null, evidence with { Source = "chat_title" },
        evidence with { Source = "remark_name" }, evidence with { Source = "nickname" }, evidence with { Source = "quoted_message" },
        evidence with { Source = "forwarded_message" }, evidence with { Speaker = "other" }, evidence with { Speaker = "unknown" },
        evidence with { Quote = "陈老师" }, evidence with { Quote = "今天有空吗？" }, evidence with { CaptureId = "unseen" } })
        Throws(() => DraftRules.ValidateSalutation(answer with { AddressEvidence = invalid }, null, captures));
    return Task.CompletedTask;
});
await Test("A nickname is usable only when direct chat evidence establishes the same form of address", () =>
{
    var answer = new ModelResponse { ObservedDisplayName = "小王", AddressTerm = "小王", GreetingText = "小王，生日快乐！" };
    Capture[] captures = [new("capture_0", [1])];
    Throws(() => DraftRules.ValidateSalutation(answer, null, captures));
    DraftRules.ValidateSalutation(answer with { AddressEvidence = ChatAddress("小王，周末有空吗？") }, null, captures);
    Throws(() => DraftRules.ValidateSalutation(answer with { AddressTerm = null }, null, captures));
    return Task.CompletedTask;
});
await Test("Legacy salutation candidates stay stored but cannot influence a new generation", async () =>
{
    var contacts = ContactRepository();
    var legacy = new Contact { DisplayName = "测试联系人", AddressTerm = "标题误当称呼", Relationship = "teacher" }; contacts.SaveContact(legacy);
    var adapter = new FakeAdapter { IsDemo = false, CanAutoAppend = false };
    var neutral = new FakeModel { ObservedName = "测试联系人" };
    var coordinator = new TaskCoordinator(contacts, examples, protector);
    var first = await coordinator.RunAsync(RealSettings(), adapter, neutral, null, default);
    Equal("成功", first!.GenerationStatus); Equal(null, neutral.LastContact!.AddressTerm); Equal("teacher", neutral.LastContact.Relationship);
    Equal(null, first.AddressTerm); Equal("标题误当称呼", contacts.Contacts("default").Single().AddressTerm);
    var inferred = new FakeModel { Response = new() { ObservedDisplayName = "测试联系人", AddressTerm = "陈老师",
        AddressEvidence = ChatAddress("陈老师，您好！"), GreetingText = "陈老师，生日快乐，愿您平安顺心！" } };
    var second = await coordinator.RunAsync(RealSettings(), adapter, inferred, null, default);
    Equal("成功", second!.GenerationStatus); Equal(legacy.Id, second.ContactId);
    var updated = contacts.Contacts("default").Single(); Equal("陈老师", updated.AddressTerm); Equal("chat_message", updated.AddressTermSource);
    Equal(updated.AddressEvidence, second.AddressEvidence); Equal("chat_message", second.AddressTermSource);
});
await Test("Unproven saved salutations are removed from both request preferences and stored-contact hints", async () =>
{
    var handler = new StubHandler(request =>
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        using var prompt = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!);
        Equal(JsonValueKind.Null, prompt.RootElement.GetProperty("salutation").GetProperty("required_address_term").ValueKind);
        Equal(JsonValueKind.Null, prompt.RootElement.GetProperty("stored_contact").GetProperty("addressTerm").ValueKind);
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = JsonSerializer.Serialize(new ModelResponse
            { ObservedDisplayName = "微信备注", GreetingText = "生日快乐，愿你平安顺心！" }, JsonDefaults.Options) } } } })) };
    });
    using var client = new CompatibleModelClient(handler);
    await client.GenerateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision", Scene = "生日" }, "", [new("0", [1])],
        new() { DisplayName = "微信备注", RemarkName = "微信备注", AddressTerm = "微信备注" }, [], true, default);
    Equal(1, handler.Calls);
});
await Test("Missing chat evidence never creates a contact, changes an old candidate or issues input", async () =>
{
    var contacts = ContactRepository(); var old = new Contact { DisplayName = "测试联系人", AddressTerm = "旧候选" }; contacts.SaveContact(old);
    var adapter = new FakeAdapter { IsDemo = false };
    var model = new FakeModel { Response = new() { ObservedDisplayName = "测试联系人", AddressTerm = "测试联系人", GreetingText = "测试联系人，生日快乐！" } };
    var result = await new TaskCoordinator(contacts, examples, protector).RunAsync(RealSettings(), adapter, model, null, default);
    Equal(2, model.Calls); Equal("失败", result!.GenerationStatus); Equal(0, adapter.Appends); Equal(null, result.AppendAttemptId);
    Equal("旧候选", contacts.Contacts("default").Single().AddressTerm); Equal("unknown", contacts.Contacts("default").Single().AddressTermSource);
});
await Test("User-entered and evidenced chat salutations remain reusable while unproven candidates do not", () =>
{
    var legacy = new Contact { AddressTerm = "小王" };
    Equal(null, DraftRules.UsableAddressTerm(legacy));
    Equal("小王", DraftRules.UsableAddressTerm(legacy with { AddressTermSource = "user" }));
    Equal("小王", DraftRules.UsableAddressTerm(legacy with { Confirmed = true }));
    Equal("小王", DraftRules.UsableAddressTerm(legacy with { AddressTermSource = "chat_message", AddressEvidence = ChatAddress("小王，最近忙吗？") }));
    Equal(null, DraftRules.UsableAddressTerm(legacy with { AddressTermSource = "chat_message", AddressEvidence = ChatAddress("小王，最近忙吗？") with { Speaker = "other" } }));
    return Task.CompletedTask;
});
await Test("Screenshot recipient mismatch blocks append after successful generation", async () =>
{
    var adapter = new FakeAdapter();
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, new FakeModel { ObservedName = "其他联系人" }, null, default);
    Equal("成功", result!.GenerationStatus); Equal(false, result.ScreenshotRecipientMatched); Equal(0, adapter.Appends);
});
await Test("Successful append records original length and verified preservation", async () =>
{
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, new FakeAdapter(), new FakeModel { ObservedName = "测试联系人" }, null, default);
    Equal(8, result!.OriginalCharacters); Equal(true, result.OriginalPreserved); Equal(true, result.ScreenshotRecipientMatched);
});
await Test("Missing screenshot title blocks real-mode append", async () =>
{
    var adapter = new FakeAdapter();
    var realSettings = new AppSettings { Scene = "生日", DemoMode = false, BaseUrl = "https://test.example/v1", AuthorizedOrigin = "https://test.example", ProtectedApiKey = protector.Protect("test-key") };
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(realSettings, adapter, new FakeModel(), null, default);
    Equal("成功", result!.GenerationStatus); Equal(0, adapter.Appends); Equal(null, result.AppendAttemptId);
});
await Test("Rate limit requires at least eight seconds cooldown", async () =>
{
    using var client = new CompatibleModelClient(new StubHandler(_ => new(HttpStatusCode.TooManyRequests)));
    try { await client.GenerateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision" }, "", [new("0", [1])], null, [], true, default); throw new Exception("expected rate limit"); }
    catch (TransientApiException e) { True(e.RetryAfter >= TimeSpan.FromSeconds(8), "missing cooldown"); }
});
await Test("Known scene validation rejects a New Year greeting for Mid-Autumn", () =>
{
    Throws(() => DraftRules.ValidateSceneGreeting("新的一年平安顺心！", "中秋"));
    DraftRules.ValidateSceneGreeting("中秋快乐，愿月圆人安！", "中秋");
    DraftRules.ValidateSceneGreeting("愿新的一岁顺心！", "生日");
    DraftRules.ValidateSceneGreeting("一切顺利！", "自定义场景");
    return Task.CompletedTask;
});
await Test("Provider quota errors do not retry or expose error message secrets", async () =>
{
    var handler = new StubHandler(_ => new(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":{\"code\":\"1308\",\"message\":\"secret-from-server\"}}") });
    using var client = new CompatibleModelClient(handler);
    var adapter = new FakeAdapter();
    var realSettings = new AppSettings { Scene = "生日", DemoMode = false, BaseUrl = "https://open.bigmodel.cn/api/paas/v4", Model = "glm-4.6v-flash", AuthorizedOrigin = "https://open.bigmodel.cn", ProtectedApiKey = protector.Protect("test-key") };
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(realSettings, adapter, client, null, default);
    Equal(1, handler.Calls); Equal(0, adapter.Appends);
    True(result!.Error.Contains("1308") && !result.Error.Contains("secret-from-server"), "unsafe or missing error detail");
});
await Test("Multiple structured outputs are rejected", async () =>
{
    var handler = new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"tool_calls\":[{},{}]}}]}") });
    using var client = new CompatibleModelClient(handler);
    try { await client.GenerateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision" }, "", [new("0", [1])], null, [], true, default); throw new Exception("multiple outputs accepted"); }
    catch (InvalidDataException) { Equal(1, handler.Calls); }
});
VisionLayout GoodLayout() => new()
{
    SchemaVersion = "1.0", Title = "测试联系人", ConversationType = "single", Evidence = "当前单人聊天工具栏和聊天信息入口",
    Blocked = false, Uncertain = false, DraftKind = "text", SelectionInInput = true,
    Chat = new() { X = 300, Y = 30, Width = 690, Height = 950 },
    Header = new() { X = 330, Y = 50, Width = 250, Height = 40 },
    Messages = new() { X = 320, Y = 120, Width = 660, Height = 550 },
    Input = new() { X = 320, Y = 700, Width = 660, Height = 260 },
    Editor = new() { X = 340, Y = 750, Width = 500, Height = 120 },
    Send = new() { X = 870, Y = 900, Width = 80, Height = 40 }
};
VisionAnchorHints GoodAnchors() => new()
{
    TopToolbar = new() { X = 750, Y = 45, Width = 180, Height = 35 },
    BottomToolbar = new() { X = 340, Y = 750, Width = 260, Height = 35 },
    TopScore = 0.95, BottomScore = 0.96, Scale = 1
};
await Test("Local toolbar anchors exclude controls without authorizing an uncertain chat", () =>
{
    var layout = VisionRules.ApplyAnchors(GoodLayout(), GoodAnchors());
    True(!VisionRules.ClickRegion(layout).Overlaps(layout.InputToolbar!), "local toolbar must not become a focus target");
    Equal(GoodLayout().Input, layout.Input); Equal(GoodLayout().Editor, layout.Editor);
    Throws(() => VisionRules.ApplyAnchors(GoodLayout() with { Uncertain = true }, GoodAnchors()));
    Throws(() => VisionRules.ApplyAnchors(GoodLayout() with { DraftKind = "rich" }, GoodAnchors()));
    Throws(() => VisionRules.ApplyAnchors(GoodLayout(), GoodAnchors() with { TopScore = 0.79 }));
    Throws(() => VisionRules.ApplyAnchors(GoodLayout(), GoodAnchors() with { Scale = double.NaN }));
    var wire = JsonSerializer.Serialize(layout, JsonDefaults.Options);
    True(!wire.Contains("inputToolbar"), "local evidence must not be model-controlled");
    Equal(null, JsonSerializer.Deserialize<VisionLayout>(wire[..^1] + ",\"inputToolbar\":{\"left\":0,\"top\":0,\"right\":1000,\"bottom\":1000}}", JsonDefaults.Options)!.InputToolbar);
    return Task.CompletedTask;
});
await Test("Template-model conflicts and lost toolbar evidence stop rechecks", () =>
{
    var anchored = VisionRules.ApplyAnchors(GoodLayout(), GoodAnchors());
    Equal(true, VisionRules.SameTarget(anchored, anchored));
    Equal(false, VisionRules.SameTarget(anchored, GoodLayout()));
    Throws(() => VisionRules.ApplyAnchors(GoodLayout(), GoodAnchors() with { BottomToolbar = GoodAnchors().BottomToolbar with { Y = 600 } }));
    Throws(() => VisionRules.ApplyAnchors(GoodLayout(), GoodAnchors() with { TopToolbar = GoodAnchors().TopToolbar with { Y = 250 } }));
    return Task.CompletedTask;
});
await Test("History scroll point uses the DPI-scaled right gutter of the confirmed message pane", () =>
{
    var layout = GoodLayout();
    var normal = VisionRules.ScrollPoint(layout, 1000, 96);
    var doubled = VisionRules.ScrollPoint(layout, 2000, 192);
    Equal(normal, doubled); Equal(layout.Messages!.Right - 23, normal.X);
    Equal(layout.Messages.Right - 46, VisionRules.ScrollPoint(layout, 1000, 192).X);
    True(normal.X > layout.Messages.X && normal.X < layout.Messages.Right && normal.Y > layout.Messages.Y && normal.Y < layout.Messages.Bottom,
        "scroll point must stay inside the message pane");
    Throws(() => VisionRules.ScrollPoint(layout, 0, 96));
    Throws(() => VisionRules.ScrollPoint(layout, 1000, 0));
    return Task.CompletedTask;
});
await Test("Visual layout rejects out-of-bounds, unavailable click areas, unsupported chats and uncertain output", () =>
{
    var valid = GoodLayout(); VisionRules.Validate(valid);
    foreach (var invalid in new[] {
        valid with { Editor = valid.Editor! with { X = 990 } },
        valid with { Editor = valid.Editor! with { X = double.NaN } },
        valid with { Send = valid.Editor }, valid with { Header = valid.Messages },
        valid with { ConversationType = "group" }, valid with { ConversationType = "unknown" },
        valid with { Blocked = true }, valid with { Uncertain = true }, valid with { DraftKind = "rich" },
        valid with { Evidence = "" }, valid with { Input = null }, valid with { SchemaVersion = "2" } })
        Throws(() => VisionRules.Validate(invalid));
    Equal(valid.Title, VisionRules.Parse("```json\n" + JsonSerializer.Serialize(valid, JsonDefaults.Options) + "\n```").Title);
    return Task.CompletedTask;
});
await Test("Visual clicks use a small inset area with a gap from the send button", () =>
{
    foreach (var layout in new[] { GoodLayout(), GoodLayout() with { Editor = new() { X = 340, Y = 750, Width = 100, Height = 25 } } })
    {
        var click = VisionRules.ClickRegion(layout); var point = VisionRules.InputPoint(layout);
        True(layout.Editor!.Contains(click) && click.X > layout.Editor.X && click.Y > layout.Editor.Y &&
            click.Right < layout.Editor.Right && click.Bottom < layout.Editor.Bottom, "click must be inside the editable canvas");
        True(click.Width <= 80 && click.Height <= 40, "click region must remain small");
        True(!click.Overlaps(layout.Send!) && (click.Right <= layout.Send!.X - 8 || click.X >= layout.Send.Right + 8 ||
            click.Bottom <= layout.Send.Y - 8 || click.Y >= layout.Send.Bottom + 8), "click must leave a gap from send");
        True(point.X > click.X && point.X < click.Right && point.Y > click.Y && point.Y < click.Bottom, "focus point must be inside the click region");
    }
    return Task.CompletedTask;
});
await Test("Canvas edge overlap is accepted only when a separate inner click area is safe", () =>
{
    var layout = GoodLayout() with { Editor = new() { X = 340, Y = 750, Width = 600, Height = 200 } };
    True(layout.Editor!.Overlaps(layout.Send!), "test requires an overlapping canvas edge");
    var parsed = VisionRules.Parse(JsonSerializer.Serialize(layout, JsonDefaults.Options));
    Equal(layout.Editor, parsed.Editor); Equal(layout.Input, parsed.Input); Equal(layout.Send, parsed.Send);
    True(!VisionRules.ClickRegion(parsed).Overlaps(parsed.Send!), "canvas overlap must never become a click overlap");
    var obstructed = layout with { Send = new() { X = 390, Y = 750, Width = 500, Height = 200 } };
    True(!VisionRules.ClickRegion(obstructed).Overlaps(obstructed.Send!), "an obstructed preferred point needs a safe inner area");
    Throws(() => VisionRules.Validate(layout with { Send = layout.Editor }));
    var narrow = GoodLayout() with { Editor = new() { X = 340, Y = 750, Width = 100, Height = 25 },
        Send = new() { X = 360, Y = 740, Width = 100, Height = 60 } };
    Throws(() => VisionRules.Validate(narrow));
    return Task.CompletedTask;
});
await Test("Visual target comparison rejects changed title and displaced input", () =>
{
    var valid = GoodLayout(); Equal(true, VisionRules.SameTarget(valid, valid));
    Equal(false, VisionRules.SameTarget(valid, valid with { Title = "其他人" }));
    Equal(false, VisionRules.SameTarget(valid, valid with { Editor = valid.Editor! with { Y = 780 } }));
    Equal(true, VisionRules.SameTarget(valid, valid with { Editor = valid.Editor! with { Width = 300, Height = 80 } }));
    return Task.CompletedTask;
});
await Test("Visual rechecks cannot move the frozen click point to a different inner area", () =>
{
    var before = GoodLayout(); var point = VisionRules.InputPoint(before);
    var after = before with { Editor = new() { X = 390, Y = 750, Width = 300, Height = 120 } };
    True(point.X > after.Editor!.X && point.X < after.Editor.Right, "old point must still be in the broad canvas");
    Equal(false, VisionRules.SameTarget(before, after));
    Equal(false, VisionRules.SameTarget(before, before with { Send = before.Editor }));
    return Task.CompletedTask;
});
await Test("Input monitoring detects changes outside the small click area and canvas", () =>
{
    VisionRegionChecks.CheckOutsideClick(GoodLayout()); return Task.CompletedTask;
});
await Test("Complete input monitoring tolerates a caret but rejects broader changes", () =>
{
    VisionRegionChecks.CheckCaretAndResize(GoodLayout()); return Task.CompletedTask;
});
await Test("Visual box wire format uses four edges and rejects width-height or text coordinates", () =>
{
    var wire = JsonSerializer.Serialize(GoodLayout(), JsonDefaults.Options);
    True(wire.Contains("\"left\"") && !wire.Contains("\"width\""), "ambiguous wire rectangle");
    Throws(() => VisionRules.Parse(wire.Replace("\"left\": 300", "\"x\": 300")));
    Throws(() => VisionRules.Parse(wire.Replace("\"left\": 300", "\"left\": \"300\"")));
    return Task.CompletedTask;
});
await Test("The message crop is restricted between title and input without repairing edit targets", () =>
{
    var valid = GoodLayout();
    var rounded = valid with { Messages = valid.Messages! with { Height = valid.Input!.Y - valid.Messages!.Y + 1 } };
    var result = VisionRules.Parse(JsonSerializer.Serialize(rounded, JsonDefaults.Options));
    True(result.Messages!.Bottom < valid.Input.Y, "message crop must move away from the draft");
    Equal(valid.Editor, result.Editor); Equal(valid.Send, result.Send);
    var overlap = VisionRules.Parse(JsonSerializer.Serialize(valid with { Messages = valid.Messages! with { Height = 700 } }, JsonDefaults.Options));
    True(overlap.Messages!.Bottom < valid.Input.Y, "overlapping proposed context must be cropped above input");
    Throws(() => VisionRules.Parse(JsonSerializer.Serialize(valid with { Messages = valid.Messages! with { Y = 750, Height = 100 } }, JsonDefaults.Options)));
    Throws(() => VisionRules.Parse(JsonSerializer.Serialize(valid with { Messages = valid.Messages! with { Height = 999 } }, JsonDefaults.Options)));
    return Task.CompletedTask;
});
await Test("A failed copy is not an empty draft without explicit focused-empty visual evidence", () =>
{
    var valid = GoodLayout(); Throws(() => VisionRules.ReadDraft(null, valid));
    Throws(() => VisionRules.ReadDraft(null, valid with { DraftKind = "empty", SelectionInInput = false }));
    Equal("", VisionRules.ReadDraft(null, valid with { DraftKind = "empty" }));
    Equal(" 原文\r\n  ", VisionRules.ReadDraft(" 原文\r\n  ", valid));
    Throws(() => VisionRules.ReadDraft("原文", valid with { SelectionInInput = false }));
    Throws(() => VisionRules.ReadDraft("文字\uFFFC", valid));
    return Task.CompletedTask;
});
await Test("Visual append preserves whitespace and pastes only the suffix once", async () =>
{
    var original = "第一行\r\n第二行  "; var pasted = ""; var reads = 0; var writes = 0;
    var result = await VisionDraftAppender.AppendAsync(original, "祝福", _ => Task.FromResult(++reads == 1 ? original : original + pasted),
        (suffix, _) => { pasted = suffix; writes++; return Task.CompletedTask; }, default);
    Equal("\r\n祝福", pasted); Equal(1, writes); Equal(2, reads); Equal(true, result.OriginalPreserved);
});
await Test("Visual append stops before paste when the complete draft changed", async () =>
{
    var writes = 0;
    var result = await VisionDraftAppender.AppendAsync("原文 ", "祝福", _ => Task.FromResult("原文"),
        (_, _) => { writes++; return Task.CompletedTask; }, default);
    Equal(0, writes); Equal("待手动处理", result.Status);
});
await Test("Visual append readback mismatch does not retry or claim preservation", async () =>
{
    var reads = 0; var writes = 0;
    var result = await VisionDraftAppender.AppendAsync("原文", "祝福", _ => Task.FromResult(++reads == 1 ? "原文" : "原文祝福"),
        (_, _) => { writes++; return Task.CompletedTask; }, default);
    Equal(1, writes); Equal("预填结果待确认", result.Status); Equal(null, result.OriginalPreserved);
});
await Test("Cancelling immediately after visual paste never issues a second write", async () =>
{
    using var cts = new CancellationTokenSource(); var writes = 0; var reads = 0;
    try
    {
        await VisionDraftAppender.AppendAsync("原文", "祝福", _ => { reads++; return Task.FromResult("原文"); },
            (_, _) => { writes++; cts.Cancel(); return Task.CompletedTask; }, cts.Token);
        throw new Exception("cancellation ignored");
    }
    catch (OperationCanceledException) { Equal(1, writes); Equal(1, reads); }
});
await Test("Fallback only activates for unavailable UIA and records the selected method", async () =>
{
    var primary = new FakeAdapter { BeginError = new UiAutomationUnavailableException("UIA absent") };
    var visual = new FakeAdapter { AutomationMethod = "vision", LayoutApiCallCount = 4, CanAutoAppend = false,
        AutoAppendUnavailableReason = "视觉生成不能复查 UIA 会话标识，未预填。" };
    using var adapter = new FallbackConversationAdapter(primary, () => visual);
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings with { DemoMode = false, EnableLiveAppend = true,
        BaseUrl = "https://test.example/v1", AuthorizedOrigin = "https://test.example" }, adapter,
        new FakeModel { ObservedName = "测试联系人" }, null, default);
    Equal("vision", result!.AutomationMethod); Equal("UIA absent", result.FallbackReason); Equal(4, result.LayoutApiCallCount); Equal(0, visual.Appends);
    Equal(null, result.AppendAttemptId); Equal(visual.AutoAppendUnavailableReason, result.Error);
});
await Test("Changed targets and cancellation do not trigger visual fallback", async () =>
{
    var factories = 0;
    using var adapter = new FallbackConversationAdapter(new FakeAdapter { BeginError = new InvalidOperationException("target changed") },
        () => { factories++; return new FakeAdapter(); });
    try { await adapter.BeginAsync(default); throw new Exception("expected stop"); }
    catch (InvalidOperationException) { Equal(0, factories); }
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    using var second = new FallbackConversationAdapter(new FakeAdapter { BeginError = new UiAutomationUnavailableException("unavailable") },
        () => { factories++; return new FakeAdapter(); });
    try { await second.BeginAsync(cancelled.Token); throw new Exception("expected cancel"); }
    catch (OperationCanceledException) { Equal(0, factories); }
});
await Test("An uncertain primary write never falls back to a second adapter", async () =>
{
    var factories = 0; var primary = new FakeAdapter { Result = new("预填结果待确认", "uncertain") };
    using var adapter = new FallbackConversationAdapter(primary, () => { factories++; return new FakeAdapter(); });
    await adapter.BeginAsync(default);
    var result = await adapter.AppendAsync(new("s", "联系人", "", "w", true), "祝福", default);
    Equal("预填结果待确认", result.Status); Equal(1, primary.Appends); Equal(0, factories);
});
await Test("Full-window vision authorization is separate and bound to the destination", async () =>
{
    var handler = new StubHandler(_ => throw new Exception("must not send")); using var client = new CompatibleModelClient(handler);
    foreach (var consent in new[] { "", "https://other.example" })
    {
        try
        {
            await client.LocateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision", AuthorizedOrigin = "https://test.example", VisionAuthorizedOrigin = consent }, "secret", new("layout", [1]), false, default);
            throw new Exception("missing consent accepted");
        }
        catch (InvalidDataException) { Equal(0, handler.Calls); }
    }
});
await Test("Visual layout uses image JSON protocol without draft functions", async () =>
{
    var handler = new StubHandler(request =>
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        True(!body.RootElement.TryGetProperty("tools", out _), "layout must not use submit_draft tools");
        Equal(0, body.RootElement.GetProperty("temperature").GetInt32());
        var instruction = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        True(instruction.Contains("safePoint") && !instruction.Contains("不应复制的旧标题"), "reference must contain only geometry, never the previous title");
        using var reference = JsonDocument.Parse(instruction[instruction.IndexOf('{')..]);
        Equal(VisionRules.ClickRegion(GoodLayout()), JsonSerializer.Deserialize<VisionBox>(reference.RootElement.GetProperty("clickRegion").GetRawText(), JsonDefaults.Options));
        Equal(VisionRules.InputPoint(GoodLayout()).X, reference.RootElement.GetProperty("safePoint").GetProperty("x").GetDouble());
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] {
            new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(GoodLayout(), JsonDefaults.Options) } } } })) };
    });
    using var client = new CompatibleModelClient(handler);
    var result = await client.LocateAsync(new() { BaseUrl = "https://open.bigmodel.cn/api/paas/v4", Model = "GLM-4.6V-Flash",
        VisionAuthorizedOrigin = "https://open.bigmodel.cn" }, "test-key", new("layout", [1]), true, default, GoodLayout() with { Title = "不应复制的旧标题" });
    Equal("测试联系人", result.Title); Equal(1, handler.Calls);
});
await Test("A layout response cannot smuggle a draft function call", async () =>
{
    using var client = new CompatibleModelClient(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
        choices = new[] { new { message = new { tool_calls = new[] { new { function = new { name = "submit_draft", arguments = JsonSerializer.Serialize(GoodLayout()) } } } } } } })) }));
    try { await client.LocateAsync(new() { BaseUrl = "https://test.example/v1", Model = "vision", VisionAuthorizedOrigin = "https://test.example" }, "", new("layout", [1]), false, default); throw new Exception("function accepted"); }
    catch (InvalidDataException) { }
});
await Test("Layout requests include local anchors while keeping model safety checks", async () =>
{
    var handler = new StubHandler(request =>
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var instruction = body.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        True(instruction.Contains("topToolbar") && instruction.Contains("bottomToolbar") && instruction.Contains("0.95"), "local geometry missing from model request");
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] {
            new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(GoodLayout(), JsonDefaults.Options) } } } })) };
    });
    using var client = new CompatibleModelClient(handler);
    var configuration = new AppSettings { BaseUrl = "https://test.example/v1", Model = "vision", VisionAuthorizedOrigin = "https://test.example" };
    var result = await client.LocateAsync(configuration, "test-key", new("layout", [1]), false, default, anchors: GoodAnchors());
    Equal(GoodAnchors().BottomToolbar, result.InputToolbar); Equal(1, handler.Calls);
    try { await client.LocateAsync(configuration, "test-key", new("layout", [1]), false, default, anchors: GoodAnchors() with { BottomScore = 0.1 }); throw new Exception("weak anchors accepted"); }
    catch (InvalidDataException) { Equal(1, handler.Calls); }
});
await Test("Clipboard cleanup warnings remain visible without replaying input", async () =>
{
    var adapter = new FakeAdapter { CleanupWarning = "原剪贴板未能恢复" };
    var result = await new TaskCoordinator(repo, examples, protector).RunAsync(settings, adapter, new FakeModel(), null, default);
    Equal(1, adapter.Appends); True(result!.Error.Contains("原剪贴板未能恢复"), "cleanup warning lost");
});
foreach (var (name, run) in ClipboardChecks.Cases())
    await Test(name, () => { run(); return Task.CompletedTask; });
await Test("Image drafts are rejected with an explicit attachment message", () =>
{
    try { VisionRules.ReadDraft(null, GoodLayout() with { DraftKind = "rich" }); throw new Exception("image draft accepted as empty"); }
    catch (InvalidDataException e) { True(e.Message.Contains("图片") && e.Message.Contains("普通文字"), "attachment limitation not explained"); }
    return Task.CompletedTask;
});
await Test("Short remark titles remain valid without reducing editor safety bounds", () =>
{
    var layout = GoodLayout();
    VisionRules.Validate(layout with { Title = "测试", Header = layout.Header! with { Width = 20, Height = 18 } });
    Throws(() => VisionRules.Validate(layout with { Editor = layout.Editor! with { Width = 30 } }));
    Throws(() => VisionRules.Validate(layout with { Header = layout.Header! with { Width = 2 } }));
    return Task.CompletedTask;
});
Console.WriteLine($"{passed} passed, {failures} failed");
return failures == 0 ? 0 : 1;

sealed class FakeAdapter : IConversationAdapter
{
    public string AutomationMethod { get; init; } = "uia";
    public string CleanupWarning { get; init; } = "";
    public int LayoutApiCallCount { get; init; }
    public bool Valid = true; public int Appends; public bool CanAutoAppend { get; set; } = true;
    public string AutoAppendUnavailableReason { get; init; } = "";
    public string StateChangeReason { get; set; } = "";
    public Func<CancellationToken, Task<bool>>? WaitForAppend { get; set; }
    public bool IsDemo { get; init; } = true;
    public bool OriginalTextRead { get; init; } = true;
    public bool CanCollectMoreContext { get; init; } = true;
    public int Scrolls;
    public Exception? BeginError { get; init; }
    public int BeginDelayMs { get; init; }
    public Action? OnBegin { get; init; }
    public FillResult Result = new("已预填", "test", true);
    public async Task<ConversationSnapshot> BeginAsync(CancellationToken ct)
    {
        OnBegin?.Invoke();
        if (BeginError is not null) throw BeginError;
        if (BeginDelayMs > 0) await Task.Delay(BeginDelayMs, ct);
        return new ConversationSnapshot("session", "测试联系人", "original", "window", IsDemo, OriginalTextRead: OriginalTextRead);
    }
    public Task<Capture> CaptureAsync(ConversationSnapshot s, int index, CancellationToken ct) => Task.FromResult(new Capture("capture_" + index, [(byte)index]));
    public Task<bool> ScrollUpAsync(ConversationSnapshot s, CancellationToken ct) { Scrolls++; return Task.FromResult(true); }
    public Task<bool> IsUnchangedAsync(ConversationSnapshot s, CancellationToken ct) => Task.FromResult(Valid);
    public Task<bool> WaitForAppendReadyAsync(ConversationSnapshot s, CancellationToken ct) => WaitForAppend?.Invoke(ct) ?? Task.FromResult(true);
    public Task<FillResult> AppendAsync(ConversationSnapshot s, string greeting, CancellationToken ct) { Appends++; return Task.FromResult(Result); }
    public Task RestoreAsync(ConversationSnapshot s) => Task.CompletedTask;
}
sealed class FakeModel : IGreetingModel
{
    public int Calls; public bool NeedsMore; public bool Invalid; public int DelayMs;
    public string? ObservedName;
    public Contact? LastContact;
    public AppSettings? LastSettings;
    public IReadOnlyList<BlessingExample> LastExamples = [];
    public bool LastFinalRound;
    public ModelResponse? Response;
    public Action? BeforeReturn;
    public async Task<ModelResponse> GenerateAsync(AppSettings s, string key, IReadOnlyList<Capture> captures, Contact? c, IReadOnlyList<BlessingExample> e, bool final, CancellationToken ct)
    {
        Calls++; LastContact = c; LastSettings = s; LastExamples = e; LastFinalRound = final;
        if (DelayMs > 0) await Task.Delay(DelayMs, ct); BeforeReturn?.Invoke();
        if (Invalid) throw new System.Text.Json.JsonException("invalid");
        return Response ?? new() { GreetingText = "生日快乐！愿你新的一岁平安顺心，每个平常的日子里都有值得期待的小事。", ObservedDisplayName = ObservedName, NeedsMoreContext = NeedsMore, ContextSufficient = !NeedsMore };
    }
}
sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return Task.FromResult(respond(request)); }
}
