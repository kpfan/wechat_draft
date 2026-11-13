using System.Text.RegularExpressions;

namespace WechatDraft.Core.ThirdVersion;

/// <summary>保留一次原始适配器基线；候选、截图、密钥和修改分支只存在内存中。</summary>
public sealed class ThirdVersionRun
{
    internal ThirdVersionRun(Guid owner, AppSettings settings, IConversationAdapter adapter, TimeProvider clock)
    {
        Owner = owner;
        InputSettings = ThirdVersionSession.Clone(settings);
        Adapter = adapter;
        Clock = clock;
        StartedTimestamp = clock.GetTimestamp();
        ExpiresAt = clock.GetUtcNow().AddMinutes(10);
        Record = new() { Scene = settings.Scene, AccountScope = settings.AccountScope, Model = settings.Model,
            IsDemo = false, GenerationStatus = "未开始", FillStatus = "待选择" };
    }

    public ThirdVersionSession? Session { get; internal set; }
    public TaskRecord Record { get; }
    public int SelectedIndex { get; internal set; } = -1;
    public DateTimeOffset ExpiresAt { get; }
    public bool Released { get; internal set; }
    public bool IsReleased => Released;
    public AppSettings Settings => ThirdVersionSession.Clone(InputSettings);
    public bool CanAppend => !Released && !Expired && !WriteAttempted && InputSettings.EnableLiveAppend &&
        Snapshot is { ForceGeneration: false, DisplayNameRead: true } snapshot && (snapshot.OriginalTextRead || snapshot.DirectAppend) && Adapter.CanAutoAppend &&
        AppendBlockReason.Length == 0 && Session is not null && SelectedIndex >= 0 && SelectedIndex < Session.Revisions.Count;
    internal Guid Owner { get; }
    internal AppSettings InputSettings { get; }
    internal IConversationAdapter Adapter { get; }
    internal ConversationSnapshot? Snapshot { get; set; }
    internal TimeProvider Clock { get; }
    internal long StartedTimestamp { get; }
    internal bool Expired => Clock.GetUtcNow() >= ExpiresAt || Clock.GetElapsedTime(StartedTimestamp) >= TimeSpan.FromMinutes(10);
    internal bool WriteAttempted { get; set; }
    internal string AppendBlockReason { get; set; } = "";
    internal bool UserAddressSelected { get; set; }
}

/// <summary>真实微信主流程：自动采集、三候选、独立修改，明确选中后才允许单次安全追加。</summary>
public sealed class ThirdVersionWorkflow(LocalRepository repository, ISecretProtector protector,
    ThirdVersionService service, TimeProvider? timeProvider = null)
{
    private readonly Guid _owner = Guid.NewGuid();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private ThirdVersionRun? _activeRun;
    public bool IsRunning => _gate.CurrentCount == 0;
    public event Action<TaskProgress>? Progress;

    public async Task<ThirdVersionRun> StartAsync(AppSettings settings, IConversationAdapter adapter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(adapter);
        var run = new ThirdVersionRun(_owner, settings, adapter, _clock);
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_activeRun is { Released: false } previous)
            {
                await ReleaseCoreAsync(previous);
                Save(previous);
            }
            _activeRun = run;
            await ExecuteAsync(run, async token =>
            {
                var input = run.InputSettings;
                if (string.IsNullOrWhiteSpace(input.Scene) || input.Scene.Length > 100 || input.ExtraInstruction.Length > 4000)
                    throw new InvalidDataException("请选择祝福场景，并检查补充要求长度。");
                run.Record.ProviderOrigin = DraftRules.Origin(input);
                if (input.AuthorizedOrigin != run.Record.ProviderOrigin)
                    throw new InvalidDataException("请先在设置中授权向当前服务上传必要的聊天截图。");
                Report("检查会话", "正在保留当前窗口、聊天标题及原始草稿基线", 8);
                run.Snapshot = await adapter.BeginAsync(token);
                var snapshot = run.Snapshot;
                var forceGeneration = snapshot.ForceGeneration;
                run.Record.DisplayName = forceGeneration ? "强制生成" : snapshot.DisplayName;
                run.Record.OriginalTextRead = snapshot.OriginalTextRead;
                if (forceGeneration)
                    run.AppendBlockReason = "强制生成跳过身份核对，本次仅可复制；请重新触发普通模式后再预填。";
                else if (!adapter.CanAutoAppend)
                    run.AppendBlockReason = adapter.AutoAppendUnavailableReason.Length > 0 ? adapter.AutoAppendUnavailableReason : "任务开始时没有自动追加能力，本次仅可复制。";
                run.Record.OriginalCharacters = snapshot.OriginalTextRead ? DraftRules.VisibleLength(snapshot.OriginalText) : 0;
                if (!forceGeneration && snapshot.DisplayNameRead && DraftRules.LooksLikeGroupTitle(snapshot.DisplayName)) throw new GroupChatDetectedException();
                if (!forceGeneration && !snapshot.DisplayNameRead)
                    throw new InvalidDataException("本地未能读取有效聊天标题，普通模式已停止；可使用强制生成并人工核对后复制。");
                if (!forceGeneration && !snapshot.OriginalTextRead && !snapshot.DirectAppend && adapter.CanAutoAppend)
                    throw new InvalidDataException("本次未读取完整草稿，不能自动追加。");
                // 强制模式不以未经核对的标题读取或绑定联系人及其历史。
                var contact = !forceGeneration && snapshot.DisplayNameRead ? repository.FindContactByName(input.AccountScope, snapshot.DisplayName) : null;
                run.Record.ContactId = contact?.Id;
                run.Record.ContactReused = contact is not null;
                run.Record.Relationship = contact?.Relationship ?? "unknown";
                run.Record.IdentityLabel = contact?.IdentityLabel;
                var recent = !forceGeneration && snapshot.DisplayNameRead
                    ? RecentGreetings(input.AccountScope, contact?.Id, snapshot.DisplayName, input.Scene, token) : [];
                var context = new GreetingGenerationContext(recent);
                const int maxCaptures = DraftRules.MaxContextCaptures;
                var captures = new List<Capture>(maxCaptures);
                Save(run);
                for (var round = 0; round < maxCaptures; round++)
                {
                    token.ThrowIfCancellationRequested();
                    // DirectAppend 首帧由视觉适配器核对/冻结，不能提前要求不存在的原稿基线。
                    if ((round != 0 || !snapshot.DirectAppend) && !await adapter.IsUnchangedAsync(snapshot, token)) throw Changed(run);
                    Report("采集上下文", round == 0 ? "采集当前聊天必要区域；预填由适配器执行草稿保护" : $"采集上滚后的消息区 {round + 1}/{maxCaptures}", 15 + round * 6);
                    var capture = await adapter.CaptureAsync(snapshot, round, token);
                    if (forceGeneration)
                        capture = capture with { ForceGeneration = true, RequiresConversationCheck = false, LocalTitle = null };
                    else
                    {
                        if (snapshot.TitleSource == "ocr" && capture.LocalTitle is null)
                            capture = capture with { LocalTitle = snapshot.DisplayName };
                        if (capture.LocalTitle is { } localTitle)
                        {
                            if (DraftRules.LooksLikeGroupTitle(localTitle)) throw new GroupChatDetectedException();
                            if (localTitle != snapshot.DisplayName || captures.Count > 0 && localTitle != captures[0].LocalTitle)
                            {
                                run.AppendBlockReason = "本地截图标题与原始会话不一致，请重新触发。";
                                throw new InvalidDataException(run.AppendBlockReason);
                            }
                        }
                        else if (captures.Count > 0 && captures[0].LocalTitle is not null)
                            throw new InvalidDataException("本地聊天标题在连续截图间缺失，未上传截图。");
                    }
                    if (captures.Any(x => x.PngBytes.AsSpan().SequenceEqual(capture.PngBytes))) break;
                    if (captures.Sum(x => (long)x.PngBytes.Length) + capture.PngBytes.Length > 12 * 1024 * 1024)
                    {
                        if (captures.Count == 0) throw new InvalidDataException("消息截图超过 12 MiB。");
                        break;
                    }
                    captures.Add(capture);
                    run.Record.CaptureCount = captures.Count;
                    run.Record.GenerationStatus = "进行中";
                    Save(run);
                    if (round + 1 == maxCaptures || !adapter.CanCollectMoreContext || !await adapter.ScrollUpAsync(snapshot, token)) break;
                    run.Record.ScrollCount++;
                }
                token.ThrowIfCancellationRequested();
                Report("提取称呼", $"正在请求 {input.Model} · {captures.Count} 张截图", 35);
                // 采集顺序完全复用上游策略：先完成有限采集，再一次性提取，不按模型称呼结果继续滚动。
                run.Session = await service.ExtractAsync(input, protector.Unprotect(input.ProtectedApiKey), captures, contact, context, token);
                CheckTitle(run);
                if (run.Session.Extraction.Basis == "unknown" && contact?.Confirmed != true)
                {
                    run.Record.GenerationStatus = "待确认称呼";
                    run.Record.FillStatus = "待手动处理";
                    run.Record.Error = "称呼不确定，请填写称呼或明确选择中性祝福；不会自动预填。";
                    Report("等待选择称呼", run.Record.Error, 75);
                    return;
                }
                Report("生成三个候选", "生成后请明确选择，尚不会预填", 80);
                await service.GenerateAsync(run.Session, null,
                    contact is { Confirmed: true } && string.IsNullOrWhiteSpace(contact.AddressTerm), token);
                Generated(run);
            }, ct);
            if (run.Session is null) { await ReleaseCoreAsync(run); Save(run); }
            return run;
        }
        finally { _gate.Release(); }
    }

    public async Task<ThirdVersionRun> GenerateAsync(ThirdVersionRun run, string? manualAddress, bool neutral, CancellationToken ct)
    {
        await WithRunAsync(run, async token =>
        {
            EnsureDraftEditable(run);
            var session = RequireSession(run);
            await service.GenerateAsync(session, manualAddress, neutral, token);
            run.UserAddressSelected = neutral || !string.IsNullOrWhiteSpace(manualAddress);
            Generated(run);
        }, ct);
        return run;
    }

    public async Task<ThirdVersionRun> RegenerateAsync(ThirdVersionRun run, CancellationToken ct)
    {
        await WithRunAsync(run, async token => { EnsureDraftEditable(run); await service.RegenerateAsync(RequireSession(run), token); Generated(run); }, ct);
        return run;
    }

    public async Task<ThirdVersionRevision?> ReviseAsync(ThirdVersionRun run, int index, string instruction, CancellationToken ct)
    {
        ThirdVersionRevision? result = null;
        await WithRunAsync(run, async token =>
        {
            EnsureDraftEditable(run);
            result = await service.ReviseAsync(RequireSession(run), index, instruction, token);
            if (run.SelectedIndex == index) UpdateSelected(run);
            run.Record.GenerationStatus = "成功";
            run.Record.GeneratedAt = _clock.GetUtcNow();
            run.Record.Error = run.AppendBlockReason;
            Report("修改完成", "仅更新此候选分支，未写入微信", 100);
        }, ct);
        return result;
    }

    public void Select(ThirdVersionRun run, int index) => WithSynchronousRun(run, () =>
    {
        EnsureDraftEditable(run);
        CheckIndex(run, index);
        run.SelectedIndex = index;
        UpdateSelected(run);
        run.Record.FillStatus = run.WriteAttempted ? run.Record.FillStatus : "待预填";
        Save(run);
    });

    public void Reset(ThirdVersionRun run, int index) => WithSynchronousRun(run, () =>
    {
        EnsureDraftEditable(run);
        CheckIndex(run, index);
        service.ResetRevision(run.Session!, index);
        if (run.SelectedIndex == index) UpdateSelected(run);
        run.Record.Error = run.AppendBlockReason;
        Save(run);
    });

    public async Task<TaskRecord> AppendSelectedAsync(ThirdVersionRun run, CancellationToken ct)
    {
        await WithRunAsync(run, async token =>
        {
            var snapshot = run.Snapshot;
            var adapter = run.Adapter;
            if (run.Released || run.Expired)
            {
                await ReleaseCoreAsync(run);
                throw new InvalidOperationException("原始会话基线已释放或超过十分钟，仅可复制；请重新触发后再预填。");
            }
            if (run.WriteAttempted || run.Record.AppendAttemptId is not null)
                throw new InvalidOperationException("本次已尝试预填，不能重试或重放；请人工检查微信。");
            if (!run.InputSettings.EnableLiveAppend) throw new InvalidOperationException("触发任务时预填未启用，本次只能复制正文。");
            if (snapshot is { ForceGeneration: true })
                throw new InvalidOperationException("强制生成没有独立核对聊天身份，本次仅可复制；不能向未经核对的聊天写入。");
            if (snapshot is not { DisplayNameRead: true } || (!snapshot.OriginalTextRead && !snapshot.DirectAppend) || !adapter.CanAutoAppend)
                throw new InvalidOperationException(adapter.AutoAppendUnavailableReason.Length > 0 ? adapter.AutoAppendUnavailableReason : "没有独立标题及完整原文或适配器直接追加能力，本次只能复制。");
            if (run.AppendBlockReason.Length > 0) throw new InvalidDataException(run.AppendBlockReason);
            CheckIndex(run, run.SelectedIndex);
            UpdateSelected(run);
            ValidateSelected(run.Record.Greeting, run.InputSettings.Scene);
            CheckTitle(run);
            if (run.AppendBlockReason.Length > 0) throw new InvalidDataException(run.AppendBlockReason);
            Report("核对输入", snapshot.DirectAppend ? "由原适配器核对目标聊天、输入位置及草稿保护，不建立新会话基线" : "核对最初的会话、焦点和完整草稿，不建立新基线", 90);
            if (!await adapter.WaitForAppendReadyAsync(snapshot, token) || !await adapter.IsUnchangedAsync(snapshot, token)) throw Changed(run);
            if (run.Expired)
            {
                await ReleaseCoreAsync(run);
                throw new InvalidOperationException("原始会话基线已超过十分钟，本次不再预填。");
            }
            token.ThrowIfCancellationRequested();
            run.WriteAttempted = true;
            run.Record.AppendAttemptId = Guid.NewGuid().ToString("N");
            run.Record.FillStatus = "写入中";
            Save(run); // 必须先持久化写入意图；数据库失败也绝不执行输入。
            token.ThrowIfCancellationRequested();
            var result = await adapter.AppendAsync(snapshot, run.Record.Greeting, token);
            run.Record.FillStatus = result.Status;
            run.Record.Error = result.Message;
            run.Record.OriginalPreserved = result.OriginalPreserved;
            if (snapshot.TitleSource == "ocr" && result.Status == "已预填" && result.OriginalPreserved == true)
                run.Record.ScreenshotRecipientMatched = true;
            run.Record.CompletedAt = _clock.GetUtcNow();
            Report("预填结束", "发送仍须由你在微信中手动确认", 100);
            await ReleaseCoreAsync(run);
        }, ct);
        return run.Record;
    }

    public async Task ReleaseAsync(ThirdVersionRun run)
    {
        CheckOwner(run);
        await _gate.WaitAsync();
        try { await ReleaseCoreAsync(run); Save(run); }
        finally { _gate.Release(); }
    }

    private async Task WithRunAsync(ThirdVersionRun run, Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        CheckOwner(run);
        await _gate.WaitAsync(CancellationToken.None);
        try { await ExecuteAsync(run, operation, ct); }
        finally { _gate.Release(); }
    }

    private async Task ExecuteAsync(ThirdVersionRun run, Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        var callsBefore = service.ApiCallCount;
        var hadWriteAttempt = run.WriteAttempted;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var operationBudget = TimeSpan.FromSeconds(Math.Clamp(run.InputSettings.TaskTimeoutSeconds, 1, 1800));
        if (!run.Released)
        {
            if (run.Expired) await ReleaseCoreAsync(run);
            else
            {
                var remainingUtc = run.ExpiresAt - _clock.GetUtcNow();
                var remainingMonotonic = TimeSpan.FromMinutes(10) - _clock.GetElapsedTime(run.StartedTimestamp);
                var remaining = remainingUtc < remainingMonotonic ? remainingUtc : remainingMonotonic;
                if (remaining < operationBudget) operationBudget = remaining;
            }
        }
        budget.CancelAfter(operationBudget > TimeSpan.Zero ? operationBudget : TimeSpan.FromMilliseconds(1));
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            await operation(budget.Token);
        }
        catch (OperationCanceledException)
        {
            run.AppendBlockReason = "原始任务已取消或超时，本次仅可复制；请重新触发后再预填。";
            await ReleaseCoreAsync(run);
            run.Record.GenerationStatus = run.Session?.Candidates.Count == 3 ? "成功" : "取消";
            if (!hadWriteAttempt) run.Record.FillStatus = run.WriteAttempted ? "预填结果待确认" : "待手动处理";
            run.Record.Error = ct.IsCancellationRequested ? "任务已取消，已保留可用内容。" : "操作超过总时间预算，已保留可用内容。";
            Report("操作停止", run.Record.Error, 100);
            if (run.WriteAttempted) await ReleaseCoreAsync(run);
        }
        catch (Exception exception)
        {
            if (exception is GroupChatDetectedException)
            {
                run.AppendBlockReason = exception.Message;
                await ReleaseCoreAsync(run);
            }
            run.Record.GenerationStatus = run.Session?.Candidates.Count == 3 ? "成功" : "失败";
            if (!hadWriteAttempt) run.Record.FillStatus = run.WriteAttempted ? "预填结果待确认" : "待手动处理";
            run.Record.Error = exception.Message;
            Report("处理失败", run.Record.Error, 100);
            if (run.WriteAttempted) await ReleaseCoreAsync(run);
        }
        finally
        {
            run.Record.ApiCallCount += Math.Max(0, service.ApiCallCount - callsBefore);
            RefreshAdapterRecord(run);
            if (run.Expired && !run.Released) await ReleaseCoreAsync(run);
            Save(run);
        }
    }

    private void WithSynchronousRun(ThirdVersionRun run, Action action)
    {
        CheckOwner(run);
        if (!_gate.Wait(0)) throw new InvalidOperationException("操作正在进行，请等待或取消后重试。");
        try { action(); }
        finally { _gate.Release(); }
    }

    private void Generated(ThirdVersionRun run)
    {
        run.SelectedIndex = -1;
        run.Record.Greeting = "";
        run.Record.GenerationStatus = "成功";
        run.Record.GeneratedAt = _clock.GetUtcNow();
        run.Record.FillStatus = run.WriteAttempted ? run.Record.FillStatus : "待选择";
        run.Record.Error = run.AppendBlockReason;
        UpdateAddress(run);
        Report("候选已生成", "请选择一条；不会自动预填或发送", 100);
    }

    private void UpdateSelected(ThirdVersionRun run)
    {
        CheckIndex(run, run.SelectedIndex);
        run.Record.Greeting = run.Session!.Revisions[run.SelectedIndex].Greeting;
        UpdateAddress(run);
    }

    private static void UpdateAddress(ThirdVersionRun run)
    {
        var session = run.Session!;
        run.Record.AddressTerm = session.ResolvedAddress;
        run.Record.AddressTermSource = session.ResolvedAddress is null ? "unknown" :
            run.UserAddressSelected || session.InputContact?.Confirmed == true ? "user" :
            session.Extraction.Basis == "direct" ? "third_version_direct" : "third_version_inferred";
        run.Record.AddressEvidence = session.ResolvedAddress is null || run.UserAddressSelected ? null : session.Extraction.EvidenceItems.FirstOrDefault();
        if (run.SelectedIndex >= 0 && run.SelectedIndex < session.Revisions.Count && session.ResolvedAddress is { } originalAddress &&
            !session.Revisions[run.SelectedIndex].Greeting.StartsWith(originalAddress, StringComparison.Ordinal))
        {
            // 用户编辑已改变称呼：不把原提取依据冒充为新称呼依据，也不反向解析正文编造新身份。
            run.Record.AddressTerm = null;
            run.Record.AddressTermSource = "user";
            run.Record.AddressEvidence = null;
        }
    }

    private static void CheckTitle(ThirdVersionRun run)
    {
        var snapshot = run.Snapshot!;
        var session = run.Session!;
        if (snapshot.ForceGeneration)
        {
            // 强制模式只生成供人工复制；模型读到的名称不能升级为独立身份或写入许可。
            run.Record.ObservedDisplayName = null;
            run.Record.ScreenshotRecipientMatched = null;
            run.AppendBlockReason = "强制生成跳过身份核对，本次仅可复制；请重新触发普通模式后再预填。";
            return;
        }
        var localTitles = session.InputCaptures.Where(x => x.LocalTitle is not null).Select(x => x.LocalTitle!).Distinct(StringComparer.Ordinal).ToArray();
        var title = snapshot.TitleSource == "ocr" ? snapshot.DisplayName : session.Extraction.ObservedDisplayName;
        run.Record.ObservedDisplayName = title;
        if (localTitles.Length > 1 || localTitles.Any(DraftRules.LooksLikeGroupTitle) || DraftRules.LooksLikeGroupTitle(title))
            throw new GroupChatDetectedException();
        if (session.InputCaptures.Any(x => x.RequiresConversationCheck) &&
            (string.IsNullOrWhiteSpace(title) || title.Length > 80 || title.Any(char.IsControl)))
            throw new InvalidDataException("截图中的完整聊天标题未能明确识别，请人工核对。");
        if (!snapshot.DisplayNameRead)
        {
            if (!string.IsNullOrWhiteSpace(title)) run.Record.DisplayName = title.Trim();
            run.Record.ScreenshotRecipientMatched = null;
            run.AppendBlockReason = "没有独立读取原始标题，模型标题不授予写入权限，仅可复制。";
            return;
        }
        if (snapshot.TitleSource != "ocr")
        {
            run.Record.ScreenshotRecipientMatched = !string.IsNullOrWhiteSpace(title) && title.Trim() == snapshot.DisplayName.Trim();
            if (run.Record.ScreenshotRecipientMatched != true)
                run.AppendBlockReason = string.IsNullOrWhiteSpace(title) ? "模型未识别完整聊天标题，保留候选但不能预填。" : "模型识别标题与原始会话不一致，保留候选但不能预填。";
        }
    }

    private static void ValidateSelected(string text, string scene)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000 || text.Any(char.IsControl) || text.Contains('`') || text.Contains("**", StringComparison.Ordinal) ||
            Regex.IsMatch(text, @"\{[^{}]*\}|\[[^\[\]]*(称呼|姓名|场景)[^\[\]]*\]"))
            throw new InvalidDataException("选中正文包含空白、控制字符、格式标记或未替换变量，不能预填。");
        ThirdVersionValidation.ValidateSceneGreeting(text, scene);
    }

    private async Task ReleaseCoreAsync(ThirdVersionRun run)
    {
        if (run.Released) return;
        run.Released = true; // 释放失败也不能再次取得写入权限。
        try { if (run.Snapshot is { } snapshot) await run.Adapter.RestoreAsync(snapshot); }
        catch { AddError(run, "恢复原始视图未完成；未重放任何输入。"); }
        finally
        {
            try { if (run.Adapter is IDisposable disposable) disposable.Dispose(); }
            catch { AddError(run, "适配器资源释放未完成。"); }
        }
        if (run.Adapter.CleanupWarning.Length > 0) AddError(run, run.Adapter.CleanupWarning);
        if (run.Expired && !run.WriteAttempted)
        {
            run.Record.FillStatus = "待手动处理";
            AddError(run, "原始会话基线已超过十分钟并释放，候选仍可复制。");
        }
        run.Record.CompletedAt = _clock.GetUtcNow();
        RefreshAdapterRecord(run);
    }

    private static void AddError(ThirdVersionRun run, string message) =>
        run.Record.Error = run.Record.Error.Length == 0 ? message : run.Record.Error + " " + message;

    private static InvalidDataException Changed(ThirdVersionRun run)
    {
        run.AppendBlockReason = run.Adapter.StateChangeReason.Length > 0 ? run.Adapter.StateChangeReason :
            "原始会话、焦点或完整草稿已变化，已停止预填，请重新触发。";
        return new(run.AppendBlockReason);
    }

    private static void EnsureDraftEditable(ThirdVersionRun run)
    {
        if (run.WriteAttempted) throw new InvalidOperationException("本次已尝试预填，历史正文不再改写；请人工核对或重新触发。");
    }

    private static ThirdVersionSession RequireSession(ThirdVersionRun run) =>
        run.Session ?? throw new InvalidOperationException("尚无可用提取结果，请重新触发采集。");

    private static void CheckIndex(ThirdVersionRun run, int index)
    {
        var session = RequireSession(run);
        if (index < 0 || index >= session.Revisions.Count || session.Candidates.Count != 3)
            throw new ArgumentOutOfRangeException(nameof(index), "请明确选择一个有效候选。");
    }

    private void CheckOwner(ThirdVersionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Owner != _owner) throw new InvalidOperationException("此任务不属于当前第三版流程。");
    }

    private static void RefreshAdapterRecord(ThirdVersionRun run)
    {
        run.Record.AutomationMethod = run.Adapter.AutomationMethod;
        run.Record.FallbackReason = run.Adapter.FallbackReason;
        run.Record.LayoutApiCallCount = run.Adapter.LayoutApiCallCount;
    }

    private IReadOnlyList<string> RecentGreetings(string scope, string? contactId, string displayName, string scene, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(displayName)) return [];
        // 上游移除了通用去重 API；仅第三版提示词保留旧的近期上下文窗口。
        var batch = repository.ReadHistoryBatch(scope, null, 200, ct);
        var recent = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in batch.Records)
        {
            ct.ThrowIfCancellationRequested();
            var sameContact = !string.IsNullOrEmpty(contactId) && !string.IsNullOrEmpty(task.ContactId)
                ? task.ContactId == contactId : task.DisplayName.Trim() == displayName.Trim();
            if (!sameContact || task.IsDemo || task.Scene.Trim() != scene.Trim() ||
                task.GenerationStatus != "成功" || string.IsNullOrWhiteSpace(task.Greeting)) continue;
            if (seen.Add(GreetingDiversity.Normalize(task.Greeting))) recent.Add(task.Greeting);
            if (recent.Count == 5) break;
        }
        return recent;
    }

    private void Save(ThirdVersionRun run) => repository.SaveTask(run.Record);
    private void Report(string stage, string detail, int percent) => Progress?.Invoke(new(stage, detail, percent));
}
