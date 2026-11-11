using System.Text.Json;

namespace WechatDraft.Core;

public sealed class TaskCoordinator(LocalRepository repository, ExampleService examples, ISecretProtector protector, string? debugRoot = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsRunning => _gate.CurrentCount == 0;
    public event Action<TaskProgress>? Progress;

    public async Task<TaskRecord?> RunAsync(AppSettings settings, IConversationAdapter adapter, IGreetingModel model,
        Contact? confirmedContact, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return null;
        settings = settings with { ApiProfiles = settings.ApiProfiles.ToList() };
        confirmedContact = confirmedContact is null ? null : confirmedContact with { };
        var taskExamples = examples.Snapshot();
        var task = new TaskRecord { Scene = settings.Scene, AccountScope = settings.AccountScope,
            IsDemo = settings.DemoMode, Model = settings.DemoMode ? "本地演示 · 无 API 调用" : settings.Model };
        using var debug = DebugSession.Begin(settings.DebugMode, debugRoot ?? Path.Combine(AppContext.BaseDirectory, "debug"), "generation");
        task.DebugDirectory = debug.DirectoryPath;
        debug.WriteJson("task-start", new { task.Id, settings.Scene, settings.Model, settings.AutomationMode,
            settings.DemoMode, settings.EnableLiveAppend, settings.TaskTimeoutSeconds });
        ConversationSnapshot? snapshot = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TaskTimeoutSeconds, 10, 180)));
        var ct = budget.Token;
        try
        {
            if (string.IsNullOrWhiteSpace(settings.Scene) || settings.Scene.Length > 100 || settings.ExtraInstruction.Length > 300)
                throw new InvalidDataException("请选择祝福场景，并检查补充要求长度。");
            if (!settings.DemoMode)
            {
                task.ProviderOrigin = DraftRules.Origin(settings);
                if (settings.AuthorizedOrigin != task.ProviderOrigin)
                    throw new InvalidDataException("请确认向当前 API 上传必要聊天截图的授权。");
            }
            Report("检查会话", "正在核对目标聊天窗口", 8);
            snapshot = await adapter.BeginAsync(ct);
            task.AutomationMethod = settings.DemoMode ? "demo" : adapter.AutomationMethod;
            task.FallbackReason = adapter.FallbackReason;
            task.LayoutApiCallCount = adapter.LayoutApiCallCount;
            task.DisplayName = snapshot.DisplayName;
            task.OriginalTextRead = snapshot.OriginalTextRead;
            if (!settings.DemoMode && snapshot.DisplayNameRead && DraftRules.LooksLikeGroupTitle(snapshot.DisplayName))
            {
                debug.Event("title-group-preflight", new { title = snapshot.DisplayName, group = true, modelCalls = 0 });
                throw new GroupChatDetectedException();
            }
            if (!snapshot.DisplayNameRead && (adapter.CanAutoAppend || snapshot.OriginalTextRead))
                throw new InvalidDataException("本次没有独立读取聊天标题，只能使用未读取草稿的只读快照。");
            if (!snapshot.OriginalTextRead && !snapshot.DirectAppend && adapter.CanAutoAppend)
                throw new InvalidDataException("本次未读取完整草稿，不能自动追加。");
            task.OriginalCharacters = snapshot.OriginalTextRead ? DraftRules.VisibleLength(snapshot.OriginalText) : 0;
            debug.WriteJson("conversation-check", new { snapshot.DisplayName, snapshot.WindowIdentity, snapshot.IsDemo,
                snapshot.OriginalTextRead, snapshot.DirectAppend, snapshot.DisplayNameRead, snapshot.TitleSource, task.OriginalCharacters,
                task.AutomationMethod, task.FallbackReason });
            // Product policy uses the visible chat name as a scoped lookup key, not a WeChat identity.
            var matchedContact = !snapshot.DisplayNameRead ? null : snapshot.IsDemo ? confirmedContact :
                repository.FindContactByName(settings.AccountScope, snapshot.DisplayName);
            var contact = DraftRules.ContactForGeneration(matchedContact);
            task.ContactId = contact?.Id;
            task.ContactReused = !snapshot.IsDemo && contact is not null;
            repository.SaveTask(task);
            var selected = taskExamples.Select(settings.Scene, contact?.Relationship ?? "unknown");
            task.ExamplesVersion = taskExamples.Version;
            task.ExampleIds = selected.Select(x => x.Id).ToArray();
            const int maxCaptures = DraftRules.MaxContextCaptures;
            var captures = new List<Capture>(maxCaptures);
            for (var round = 0; round < maxCaptures; round++)
            {
                ct.ThrowIfCancellationRequested();
                // The visual adapter validates the first capture itself, or uses the already validated frozen frame.
                if ((round != 0 || !snapshot.DirectAppend) && !await adapter.IsUnchangedAsync(snapshot, ct))
                    throw new InvalidDataException(adapter.StateChangeReason.Length > 0 ? adapter.StateChangeReason : "会话或输入状态已变化，请重新触发。");
                var captureDetail = round == 0 ? "采集当前消息区" : $"采集上滚后的消息区 {round + 1}/{maxCaptures}";
                Report("采集上下文", captureDetail +
                    (snapshot.OriginalTextRead ? $" · 保留原有 {task.OriginalCharacters} 个字符" : " · 生成前未读取草稿"), 15 + round * 6);
                var capture = await adapter.CaptureAsync(snapshot, round, ct);
                if (capture.LocalTitle is not null && (!snapshot.DisplayNameRead || capture.LocalTitle != snapshot.DisplayName) ||
                    captures.Count > 0 && capture.LocalTitle != captures[0].LocalTitle)
                    throw new InvalidDataException("本地聊天标题与本次会话不一致，未上传截图。");
                if (!snapshot.DisplayNameRead) capture = capture with { RequiresConversationCheck = true };
                if (captures.Any(previous => capture.PngBytes.AsSpan().SequenceEqual(previous.PngBytes))) break;
                if (captures.Sum(previous => (long)previous.PngBytes.Length) + capture.PngBytes.Length > 12 * 1024 * 1024)
                {
                    if (captures.Count == 0) throw new InvalidDataException("消息截图超过 12 MiB。");
                    break;
                }
                if (settings.DemoMode) debug.WriteJson("demo-context", new { round, capture.Id, synthetic = true });
                else debug.WriteBytes("context-" + round, capture.PngBytes, ".png");
                captures.Add(capture);
                task.CaptureCount = captures.Count;
                if (round + 1 == maxCaptures || !adapter.CanCollectMoreContext || !await adapter.ScrollUpAsync(snapshot, ct)) break;
                task.ScrollCount++;
            }

            ModelResponse? answer;
            var extraUsed = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Report("生成祝福", settings.DemoMode ? "本地演示生成中" : $"正在请求 {settings.Model} · {captures.Count} 张截图", 35);
                if (!settings.DemoMode) task.ApiCallCount++;
                task.GenerationStatus = "进行中"; repository.SaveTask(task);
                try
                {
                    answer = await model.GenerateAsync(settings, settings.DemoMode ? "" : protector.Unprotect(settings.ProtectedApiKey),
                        captures, contact, selected, finalRound: true, ct);
                    DraftRules.ValidateGreeting(answer);
                    DraftRules.ValidateConversation(answer, captures);
                    DraftRules.ValidateSalutation(answer, contact, captures, snapshot.TitleSource == "ocr" ? snapshot.DisplayName : null);
                    break;
                }
                catch (Exception e) when (!extraUsed && (e is TransientApiException or JsonException or InvalidDataException))
                {
                    if (e is InvalidDataException && e.Message.StartsWith("API ", StringComparison.Ordinal)) throw;
                    extraUsed = true;
                    var delay = e is TransientApiException transient ? transient.RetryAfter : TimeSpan.FromMilliseconds(300);
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    debug.Event("model-retry", new { type = e.GetType().Name, e.Message, delayMs = delay.TotalMilliseconds });
                    Report("重试请求", "本任务最多额外请求一次", 40);
                    await Task.Delay(delay, ct);
                }
            }
            if (answer is null) throw new InvalidDataException("没有获得可用的祝福正文。");
            task.Greeting = answer.GreetingText; task.GeneratedAt = DateTimeOffset.UtcNow; task.GenerationStatus = "成功";
            debug.WriteJson("greeting-parsed", answer);
            var titleCheckedLocally = captures[0].LocalTitle is not null;
            task.ObservedDisplayName = titleCheckedLocally ? snapshot.DisplayName : answer.ObservedDisplayName;
            if (!titleCheckedLocally && !settings.DemoMode && string.IsNullOrWhiteSpace(answer.ObservedDisplayName))
                throw new InvalidDataException("模型没有识别出截图中的聊天标题，已保留生成内容并停止预填。");
            if (!snapshot.DisplayNameRead && (answer.ObservedDisplayName is not { Length: > 0 and <= 80 } || answer.ObservedDisplayName.Any(char.IsControl)))
                throw new InvalidDataException("截图中的聊天标题未能明确识别。生成内容已保留，请人工核对。");
            if (!titleCheckedLocally && snapshot.DisplayNameRead && !string.IsNullOrWhiteSpace(answer.ObservedDisplayName))
            {
                task.ScreenshotRecipientMatched = answer.ObservedDisplayName.Trim() == snapshot.DisplayName.Trim();
                if (task.ScreenshotRecipientMatched == false)
                    throw new InvalidDataException("模型识别的聊天标题与当前会话不一致，已保留生成内容并停止预填。");
            }
            if (!snapshot.DisplayNameRead)
            {
                // A model-read title describes the frozen frame; it is not an independent identity.
                // Do not use it to read or mutate a saved contact or authorize a write.
                task.DisplayName = answer.ObservedDisplayName!.Trim();
                task.ScreenshotRecipientMatched = null;
                task.Relationship = answer.RelationshipType;
                task.IdentityLabel = answer.IdentityLabel;
                task.AddressTerm = DraftRules.ResolveAddressTerm(null, answer);
                task.AddressTermSource = task.AddressTerm is null ? "unknown" : "chat_message";
                task.AddressEvidence = task.AddressTerm is null ? null : answer.AddressEvidence;
            }
            else
            {
                var resolvedContact = matchedContact is null
                    ? new Contact { AccountScope = settings.AccountScope, DisplayName = snapshot.DisplayName.Trim(),
                        RemarkName = snapshot.IsDemo ? null : snapshot.DisplayName.Trim(),
                        BindingStatus = snapshot.IsDemo ? "candidate" : "name_match", IsDemo = snapshot.IsDemo }
                    : matchedContact with { };
                if (!resolvedContact.Confirmed)
                {
                    if (resolvedContact.Relationship == "unknown") resolvedContact.Relationship = answer.RelationshipType;
                    if (string.IsNullOrWhiteSpace(resolvedContact.IdentityLabel)) resolvedContact.IdentityLabel = answer.IdentityLabel;
                    if (DraftRules.UsableAddressTerm(resolvedContact) is null && !string.IsNullOrWhiteSpace(answer.AddressTerm))
                    {
                        // Validation above requires direct self-to-recipient message evidence for new terms.
                        resolvedContact.AddressTerm = answer.AddressTerm.Trim();
                        resolvedContact.AddressTermSource = "chat_message";
                        resolvedContact.AddressEvidence = answer.AddressEvidence;
                    }
                }
                if (!snapshot.IsDemo)
                {
                    if (string.IsNullOrWhiteSpace(resolvedContact.RemarkName)) resolvedContact.RemarkName = snapshot.DisplayName.Trim();
                    resolvedContact.BindingStatus = "name_match";
                }
                repository.SaveContact(resolvedContact);
                task.ContactId = resolvedContact.Id;
                task.Relationship = resolvedContact.Relationship;
                task.IdentityLabel = resolvedContact.IdentityLabel;
                task.AddressTerm = DraftRules.UsableAddressTerm(resolvedContact);
                task.AddressTermSource = task.AddressTerm is null ? "unknown" : resolvedContact.Confirmed ? "user" : resolvedContact.AddressTermSource;
                task.AddressEvidence = task.AddressTerm is null ? null : resolvedContact.AddressEvidence;
            }
            repository.SaveTask(task);
            ct.ThrowIfCancellationRequested();
            Report("核对输入", snapshot.DirectAppend ? "核对目标会话和输入框位置" : "检查会话、焦点和原文是否仍一致", 86);
            if (!settings.DemoMode && !settings.EnableLiveAppend)
            {
                task.FillStatus = "待手动处理"; task.Error = "预填已关闭。生成内容已保留，可复制正文。";
            }
            else if (!snapshot.DisplayNameRead || !adapter.CanAutoAppend)
            {
                task.FillStatus = "待手动处理";
                task.Error = adapter.AutoAppendUnavailableReason.Length > 0 ? adapter.AutoAppendUnavailableReason : "自动追加未启用。请检查生成内容后复制。";
            }
            else if (!snapshot.OriginalTextRead && !snapshot.DirectAppend)
            {
                task.FillStatus = "待手动处理"; task.Error = "本次未读取完整草稿，不能自动追加。请检查生成内容后复制。";
            }
            else if (!await adapter.WaitForAppendReadyAsync(snapshot, ct) || !await adapter.IsUnchangedAsync(snapshot, ct))
            {
                task.FillStatus = "待手动处理";
                task.Error = adapter.StateChangeReason.Length > 0 ? adapter.StateChangeReason : "会话、焦点或原文已变化。请查看结果并自行复制。";
            }
            else
            {
                task.AppendAttemptId = Guid.NewGuid().ToString("N"); task.FillStatus = "写入中";
                repository.SaveTask(task); // Durable intent before issuing any input.
                ct.ThrowIfCancellationRequested();
                var result = await adapter.AppendAsync(snapshot, task.Greeting, ct);
                debug.WriteJson("append-result", result);
                task.FillStatus = result.Status; task.Error = result.Message;
                task.OriginalPreserved = result.OriginalPreserved;
                if (snapshot.TitleSource == "ocr" && result.Status == "已预填" && result.OriginalPreserved == true)
                    task.ScreenshotRecipientMatched = true;
            }
            Report("处理完成", task.FillStatus, 100);
        }
        catch (OperationCanceledException)
        {
            debug.Event("cancelled", new { userCancelled = cancellationToken.IsCancellationRequested });
            task.GenerationStatus = task.GenerationStatus == "成功" ? "成功" : "取消";
            task.FillStatus = task.AppendAttemptId is null ? "待手动处理" : "预填结果待确认";
            task.Error = cancellationToken.IsCancellationRequested ? "任务已取消。" : "任务超过总时间预算。";
            Report(cancellationToken.IsCancellationRequested ? "已取消" : "已超时", task.Error, 100);
        }
        catch (Exception e)
        {
            debug.WriteJson("task-error", new { type = e.GetType().Name, e.Message, e.StackTrace });
            task.GenerationStatus = task.GenerationStatus == "成功" ? "成功" : "失败";
            task.FillStatus = task.AppendAttemptId is null ? "未预填" : "预填结果待确认";
            task.Error = e is JsonException ? "模型 JSON 格式无效。" : e.Message;
            Report(task.CaptureCount == 0 && task.ApiCallCount == 0 ? "检查失败" : "处理失败", task.Error, 100);
        }
        finally
        {
            if (snapshot is not null) { try { await adapter.RestoreAsync(snapshot); } catch { /* Never undo user activity. */ } }
            else if (adapter is IDisposable disposable) { try { disposable.Dispose(); } catch { /* Release a partially initialized adapter. */ } }
            if (adapter.CleanupWarning.Length > 0) task.Error = (task.Error.Length > 0 ? task.Error + " " : "") + adapter.CleanupWarning;
            task.AutomationMethod = settings.DemoMode ? "demo" : adapter.AutomationMethod;
            task.FallbackReason = adapter.FallbackReason;
            task.LayoutApiCallCount = adapter.LayoutApiCallCount;
            task.CompletedAt = DateTimeOffset.UtcNow;
            debug.WriteJson("task-result", task);
            if (debug.Warning is { Length: > 0 } warning)
                task.Error = (task.Error.Length > 0 ? task.Error + " " : "") + "调试记录未完整保存：" + warning;
            try { repository.SaveTask(task); }
            finally { _gate.Release(); }
        }
        return task;
    }
    private void Report(string stage, string detail, int percent)
    {
        DebugSession.Current?.Event(stage, new { detail, percent });
        Progress?.Invoke(new(stage, detail, percent));
    }
}
