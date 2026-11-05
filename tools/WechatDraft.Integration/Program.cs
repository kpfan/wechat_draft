using System.Diagnostics;
using System.Text.Json;
using WechatDraft.Core;
using WechatDraft.Windows;

// Developer integration commands. Credentials are accepted only via stdin and never echoed.
// No mouse, keyboard, chat selection or message sending commands are exposed here.
var mode = args.FirstOrDefault() ?? "vision";
var data = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("artifacts/app/data");
try
{
    if (mode == "uia-prefill-check")
    {
        var windows = WechatAdapter.FindWindows();
        if (windows.Count != 1) throw new InvalidDataException("需要唯一可见微信窗口才能只读检查预填身份。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var check = await WechatAdapter.InspectConversationIdentityAsync(windows[0].Handle, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { conversationIdentityAvailable = check.Available, reason = check.Reason,
            bindingMethod = check.BindingMethod, selectedSessionAvailable = check.SelectedSessionAvailable,
            identityRechecked = check.Rechecked, inputIssued = false }));
        return 0;
    }
    if (mode == "uia-regions")
    {
        if (args.Length < 2) throw new InvalidDataException("请指定标注图输出目录。");
        var windows = WechatAdapter.FindWindows();
        var window = args.Length > 2 && long.TryParse(args[2], out var handle)
            ? windows.SingleOrDefault(item => item.Handle.ToInt64() == handle)
            : windows.Count == 1 ? windows[0] : null;
        if (window is null) throw new InvalidDataException($"发现 {windows.Count} 个微信窗口；需唯一目标或指定有效窗口句柄。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exported = await WechatAdapter.ExportRegionsAsync(window.Handle, timeout.Token);
        Directory.CreateDirectory(data);
        var previewPath = Path.Combine(data, "uia-regions.png");
        var coordinatesPath = Path.Combine(data, "uia-regions.json");
        await File.WriteAllBytesAsync(previewPath, exported.PreviewPng);
        await File.WriteAllTextAsync(coordinatesPath, JsonSerializer.Serialize(exported.Regions, JsonDefaults.Options));
        Console.WriteLine(JsonSerializer.Serialize(new { previewPath, coordinatesPath, scrollControlFound = exported.Regions.ScrollControl is not null }));
        return 0;
    }
    if (mode == "clipboard-formats")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { formats = ClipboardDiagnostics.Formats() }));
        return 0;
    }
    if (mode == "probe-interactive" &&
        (Path.TrimEndingDirectorySeparator(data).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath("artifacts/app/data")), StringComparison.OrdinalIgnoreCase) ||
         File.Exists(Path.Combine(data, "drafts.db"))))
        throw new InvalidDataException("Probe requires a new isolated data directory without an existing drafts.db.");
    if (mode == "use-volcengine" && (args.Length < 3 || !File.Exists(Path.Combine(data, "drafts.db")) ||
        !File.Exists(Path.Combine(Path.GetFullPath(args[2]), "drafts.db")) ||
        Path.TrimEndingDirectorySeparator(data).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2])), StringComparison.OrdinalIgnoreCase)))
        throw new InvalidDataException("Provider switch requires distinct existing destination and source profiles.");
    var protector = new DpapiProtector();
    var repository = new LocalRepository(data, protector);
    var settings = repository.LoadSettings();
    if (mode == "salutation-status")
    {
        var contacts = repository.Contacts(settings.AccountScope).Where(contact => !contact.IsDemo).ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            realContacts = contacts.Length,
            reusableChatSalutations = contacts.Count(contact => !contact.Confirmed && contact.AddressTermSource == "chat_message" && DraftRules.UsableAddressTerm(contact) is not null),
            userSalutations = contacts.Count(contact => (contact.Confirmed || contact.AddressTermSource == "user") && DraftRules.UsableAddressTerm(contact) is not null),
            pendingSalutations = contacts.Count(contact => !string.IsNullOrWhiteSpace(contact.AddressTerm) && DraftRules.UsableAddressTerm(contact) is null)
        }));
        return 0;
    }
    if (mode is "debug-on" or "debug-off")
    {
        settings.DebugMode = mode == "debug-on" && DebugSession.IsSupported;
        repository.SaveSettings(settings);
        Console.WriteLine(JsonSerializer.Serialize(new { settings.DebugMode, supported = DebugSession.IsSupported }));
        return 0;
    }
    if (mode == "use-volcengine")
    {
        var source = new LocalRepository(Path.GetFullPath(args[2]), protector).LoadSettings();
        if (source.BaseUrl.TrimEnd('/') != "https://ark.cn-beijing.volces.com/api/plan/v3" || source.ApiPath != "chat/completions" ||
            source.Model != "doubao-seed-2.1-lite" || source.ProtectedApiKey.Length == 0)
            throw new InvalidDataException("Source is not the validated Volcengine vision profile.");
        var key = protector.Unprotect(source.ProtectedApiKey);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Source credential is empty.");
        var backupFolder = Path.GetFullPath(".tools/provider-backups"); Directory.CreateDirectory(backupFolder);
        var backupPath = Path.Combine(backupFolder, "settings-before-volcengine-" + Guid.NewGuid().ToString("N") + ".protected");
        File.WriteAllText(backupPath, protector.Protect(JsonSerializer.Serialize(settings, JsonDefaults.Options)));
        var candidate = settings with {
            BaseUrl = source.BaseUrl, ApiPath = source.ApiPath, Model = source.Model,
            ProtectedApiKey = protector.Protect(key), RequestTimeoutSeconds = 60,
            TaskTimeoutSeconds = Math.Max(settings.TaskTimeoutSeconds, 150), DemoMode = false
        };
        var origin = DraftRules.Origin(candidate);
        var authorizeScreens = args.Contains("--authorize-screens", StringComparer.Ordinal);
        candidate.AuthorizedOrigin = authorizeScreens ? origin : "";
        candidate.VisionAuthorizedOrigin = authorizeScreens ? origin : "";
        // This command is an explicit provider change. A probe failure must never fall back to GLM.
        repository.SaveSettings(candidate);
        Console.WriteLine(JsonSerializer.Serialize(new { switched = true, origin, candidate.Model,
            candidate.AutomationMode, candidate.EnableLiveAppend, screenshotsAuthorized = authorizeScreens, backupPath }));
        using var client = new CompatibleModelClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var (png, code) = VisionTestImage.Create(); var watch = Stopwatch.StartNew();
        await client.TestVisionAsync(candidate, key, png, code, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "vision", passed = true, elapsedMs = watch.ElapsedMilliseconds }));
        var (conversation, name) = VisionTestImage.CreateConversation(); watch.Restart();
        var testSettings = candidate with { Scene = "生日", ExtraInstruction = "" };
        var exampleService = new ExampleService(); exampleService.Load(Path.GetFullPath("config/blessing_examples.json"));
        var answer = await client.GenerateAsync(testSettings, key, [new("synthetic_only", conversation)], null,
            exampleService.Select(testSettings.Scene, "unknown"), true, timeout.Token);
        if (answer.ObservedDisplayName?.Trim() != name) throw new InvalidDataException("Synthetic greeting title mismatch; Volcengine remains selected.");
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "greeting", passed = true, titleMatched = true,
            elapsedMs = watch.ElapsedMilliseconds, input = "synthetic-images-only", apiCalls = 2, liveInputAttempted = false }));
        return 0;
    }
    if (mode == "settings-status")
    {
        var origin = DraftRules.Origin(settings);
        Console.WriteLine(JsonSerializer.Serialize(new {
            origin, settings.Model, settings.AutomationMode, settings.DemoMode, settings.EnableLiveAppend, settings.WindowLayout,
            chatScreenshotAuthorized = settings.AuthorizedOrigin == origin,
            fullWindowAuthorized = settings.VisionAuthorizedOrigin == origin,
            hasStoredChatConsent = settings.AuthorizedOrigin.Length > 0,
            hasStoredVisionConsent = settings.VisionAuthorizedOrigin.Length > 0
        }));
        return 0;
    }
    if (mode is "configure" or "configure-interactive" or "probe-interactive")
    {
        string input;
        if (mode is "configure-interactive" or "probe-interactive")
        {
            Console.WriteLine("READY_FOR_SECRET_NO_ECHO");
            var buffer = new System.Text.StringBuilder();
            while (true)
            {
                var character = Console.ReadKey(intercept: true);
                if (character.Key == ConsoleKey.Enter) break;
                if (character.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; }
                else if (!char.IsControl(character.KeyChar)) buffer.Append(character.KeyChar);
            }
            input = buffer.ToString(); buffer.Clear();
        }
        else input = await Console.In.ReadToEndAsync();
        using var document = JsonDocument.Parse(input);
        var key = document.RootElement.GetProperty("apiKey").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Missing API key on stdin.");
        if (mode == "probe-interactive")
        {
            settings = new AppSettings {
                BaseUrl = "https://ark.cn-beijing.volces.com/api/plan/v3", Model = "doubao-seed-2.1-lite",
                ProtectedApiKey = protector.Protect(key), DemoMode = false, EnableLiveAppend = false,
                RequestTimeoutSeconds = 60, TaskTimeoutSeconds = 150, Scene = "生日"
            };
            repository.SaveSettings(settings);
            using var client = new CompatibleModelClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var (png, code) = VisionTestImage.Create();
            var visionWatch = Stopwatch.StartNew();
            await client.TestVisionAsync(settings, key, png, code, timeout.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "vision", passed = true, elapsedMs = visionWatch.ElapsedMilliseconds, model = settings.Model }));
            var (conversation, name) = VisionTestImage.CreateConversation();
            var generationWatch = Stopwatch.StartNew();
            var examples = new ExampleService(); examples.Load(Path.GetFullPath("config/blessing_examples.json"));
            var result = await client.GenerateAsync(settings, key, [new("synthetic_0", conversation)], null,
                examples.Select(settings.Scene, "unknown"), true, timeout.Token);
            var titleMatched = result.ObservedDisplayName?.Trim() == name;
            if (!titleMatched) throw new InvalidDataException("Synthetic conversation title did not match; no WeChat input was attempted.");
            Console.WriteLine(JsonSerializer.Serialize(new { stage = "greeting", passed = true, titleMatched,
                scene = settings.Scene, greeting = result.GreetingText, relationship = result.RelationshipType,
                addressTerm = result.AddressTerm, elapsedMs = generationWatch.ElapsedMilliseconds,
                model = settings.Model, apiCalls = 2, input = "synthetic-images-only", liveAppend = false }));
            return 0;
        }
        settings.BaseUrl = "https://open.bigmodel.cn/api/paas/v4";
        settings.ApiPath = "chat/completions";
        settings.Model = "glm-4.6v-flash";
        settings.ProtectedApiKey = protector.Protect(key);
        settings.AuthorizedOrigin = DraftRules.Origin(settings);
        settings.DemoMode = false;
        settings.EnableLiveAppend = true;
        settings.RequestTimeoutSeconds = 60;
        settings.TaskTimeoutSeconds = 150;
        repository.SaveSettings(settings);
        Console.WriteLine(JsonSerializer.Serialize(new { configured = true, origin = settings.AuthorizedOrigin, model = settings.Model }));
        return 0;
    }
    if (mode == "layout-bottom-image-synthetic")
    {
        using var client = new CompatibleModelClient();
        var syntheticSettings = settings with { VisionAuthorizedOrigin = DraftRules.Origin(settings) };
        var (png, _) = VisionTestImage.CreateWindow(bottomToolbar: true, imageDraft: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await client.LocateAsync(syntheticSettings, protector.Unprotect(settings.ProtectedApiKey), new("synthetic_image_attachment", png), false, timeout.Token);
            throw new Exception("Synthetic image attachment was not rejected.");
        }
        catch (InvalidDataException e) when (e.Message.Contains("其他非文字草稿"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { imageDraftRejected = true, apiCalls = 1, model = settings.Model, input = "synthetic-image-only", liveAppend = false }));
            return 0;
        }
    }
    if (mode is "layout-flow-synthetic" or "layout-bottom-flow-synthetic" or "layout-bottom-short-flow-synthetic")
    {
        using var client = new CompatibleModelClient();
        var syntheticSettings = settings with { VisionAuthorizedOrigin = DraftRules.Origin(settings) };
        var key = protector.Unprotect(settings.ProtectedApiKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var watch = Stopwatch.StartNew();
        var bottomToolbar = mode != "layout-flow-synthetic";
        var (firstPng, name) = VisionTestImage.CreateWindow(bottomToolbar: bottomToolbar, displayName: mode == "layout-bottom-short-flow-synthetic" ? "测试" : null);
        var first = await client.LocateAsync(syntheticSettings, key, new("synthetic_window", firstPng), false, timeout.Token);
        var (selectedPng, _) = VisionTestImage.CreateWindow(selected: true, displayName: name, bottomToolbar: bottomToolbar);
        var second = await client.LocateAsync(syntheticSettings, key, new("synthetic_selection", selectedPng), true, timeout.Token, first);
        if (first.Title.Trim() != name || second.Title.Trim() != name || !second.SelectionInInput || !VisionRules.SameTarget(first, second))
        {
            Console.WriteLine(JsonSerializer.Serialize(new { firstTitleMatched = first.Title.Trim() == name, secondTitleMatched = second.Title.Trim() == name,
                second.SelectionInInput, sameTarget = VisionRules.SameTarget(first, second), firstInput = first.Input, secondInput = second.Input,
                firstHeader = first.Header, secondHeader = second.Header, firstEditor = first.Editor, secondEditor = second.Editor, firstSend = first.Send, secondSend = second.Send }));
            throw new InvalidDataException("Synthetic layout/selection did not retain the same title and safe input target.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { flowPassed = true, titleMatched = true, sameTarget = true, second.SelectionInInput,
            firstEditor = first.Editor, selectedEditor = second.Editor, elapsedMs = watch.ElapsedMilliseconds,
            model = settings.Model, apiCalls = 2, input = "synthetic-images-only", liveAppend = false }));
        return 0;
    }
    if (mode is "layout-synthetic" or "layout-selection-synthetic" or "layout-empty-synthetic")
    {
        using var client = new CompatibleModelClient();
        var syntheticSettings = settings with { VisionAuthorizedOrigin = DraftRules.Origin(settings) };
        var checkingSelection = mode != "layout-synthetic";
        var (png, expectedTitle) = VisionTestImage.CreateWindow(checkingSelection, mode == "layout-empty-synthetic");
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var result = await client.LocateAsync(syntheticSettings, protector.Unprotect(settings.ProtectedApiKey),
            new("synthetic_window_only", png), checkingSelection, timeout.Token);
        if (result.Title.Trim() != expectedTitle) throw new InvalidDataException("Synthetic layout title mismatch.");
        if (checkingSelection && !result.SelectionInInput) throw new InvalidDataException("Synthetic focused input was not recognized.");
        if (mode == "layout-empty-synthetic" && result.DraftKind != "empty") throw new InvalidDataException("Synthetic empty input was not recognized.");
        Console.WriteLine(JsonSerializer.Serialize(new { layoutPassed = true, titleMatched = true, result.ConversationType,
            result.DraftKind, result.SelectionInInput, result.Chat, result.Editor, result.Send, elapsedMs = watch.ElapsedMilliseconds,
            model = settings.Model, input = "synthetic-image-only", liveAppend = false }));
        return 0;
    }
    if (mode == "vision")
    {
        using var client = new CompatibleModelClient();
        var (png, code) = VisionTestImage.Create();
        var watch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        await client.TestVisionAsync(settings, protector.Unprotect(settings.ProtectedApiKey), png, code, timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(new { visionPassed = true, elapsedMs = watch.ElapsedMilliseconds, model = settings.Model }));
        return 0;
    }
    if (mode == "summary")
    {
        var latest = repository.History(settings.AccountScope, 10).FirstOrDefault(x => !x.IsDemo);
        Console.WriteLine(JsonSerializer.Serialize(new {
            latest?.GenerationStatus, latest?.FillStatus, latest?.Error, latest?.Scene, latest?.CaptureCount,
            latest?.ScrollCount, latest?.ApiCallCount, latest?.LayoutApiCallCount, latest?.AutomationMethod, latest?.FallbackReason, latest?.Model,
            latest?.OriginalCharacters, latest?.OriginalPreserved, latest?.ScreenshotRecipientMatched, latest?.ContactReused,
            greetingCharacters = latest is null ? 0 : DraftRules.VisibleLength(latest.Greeting),
            elapsedMs = latest?.CompletedAt is DateTimeOffset completed ? (long?)(completed - latest.StartedAt).TotalMilliseconds : null
        }));
        return 0;
    }
    if (mode == "summaries")
    {
        Console.WriteLine(JsonSerializer.Serialize(repository.History(settings.AccountScope, 10).Where(x => !x.IsDemo).Select(x => new {
            x.Id, x.StartedAt, x.CompletedAt, x.GenerationStatus, x.FillStatus, x.Error, x.CaptureCount,
            x.ScrollCount, x.ApiCallCount, x.Model, x.OriginalCharacters, x.OriginalPreserved, x.ScreenshotRecipientMatched, x.ContactReused,
            greetingCharacters = DraftRules.VisibleLength(x.Greeting)
        })));
        return 0;
    }
    if (mode == "recipient-check")
    {
        var latest = repository.History(settings.AccountScope, 10).FirstOrDefault(x => !x.IsDemo);
        Console.WriteLine(JsonSerializer.Serialize(new { latest?.DisplayName, latest?.ObservedDisplayName, latest?.ScreenshotRecipientMatched }));
        return 0;
    }
    throw new InvalidDataException("Unknown command.");
}
catch (Exception e)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = e.GetType().Name, message = e.Message }));
    return 1;
}
