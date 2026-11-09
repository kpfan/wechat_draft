using WechatDraft.Core;
using WechatDraft.Windows;

internal static class VisionInputRecheckChecks
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string root)
    {
        yield return ("Unchanged input images do not request extra draft copies", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", " 原文\r\n");
            var state = new VisionInputRecheck(guard, Frame(32), 192);
            state.Observe(Frame(32));
            if (!await state.ConfirmAsync(_ => throw new Exception("Unchanged input was copied."),
                    _ => throw new Exception("Unchanged input was settled."), default) || state.Pending)
                throw new Exception("An unchanged input was blocked.");
        });
        yield return ("Visual decoration changes preserve prefill after the full original draft matches", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", " 原文\r\n");
            var state = new VisionInputRecheck(guard, Frame(32), 192);
            var changed = Frame(64); var reads = 0; var settles = 0;
            state.Observe(changed); state.Observe(Frame(96));
            if (!state.Pending || !guard.Valid) throw new Exception("An image change invalidated text without checking it.");
            if (!await state.ConfirmAsync(_ => { reads++; return Copied(" 原文\r\n"); },
                    _ => { settles++; return Task.FromResult(changed); }, default))
                throw new Exception("Matching text was rejected for a visual change.");
            state.Observe(changed);
            if (reads != 1 || settles != 1 || state.Pending || !guard.TryIssuePaste())
                throw new Exception("The verified image was not adopted or repeat observations caused extra copies.");
        });
        yield return ("A transient image returning to baseline still requires a full draft recheck", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", "原文"); var baseline = Frame(32);
            var state = new VisionInputRecheck(guard, baseline, 96); var reads = 0;
            state.Observe(Frame(64)); state.Observe(baseline);
            await state.ConfirmAsync(_ => { reads++; return Copied("原文"); }, _ => Task.FromResult(baseline), default);
            if (reads != 1) throw new Exception("A reverted image bypassed the requested text check.");
        });
        yield return ("A changed original draft prevents prefill permanently even if its image later returns", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", "原文 ");
            var state = new VisionInputRecheck(guard, Frame(32), 96);
            state.Observe(Frame(64));
            if (await state.ConfirmAsync(_ => Copied("原文"),
                    _ => throw new Exception("Changed text refreshed its baseline."), default))
                throw new Exception("A whitespace edit was accepted.");
            if (await state.ConfirmAsync(_ => throw new Exception("A rejected draft was reread."),
                    _ => throw new Exception("A rejected draft was settled."), default) || guard.TryIssuePaste())
                throw new Exception("Restoring text could recover a rejected write.");
        });
        yield return ("A title change during draft recheck cannot authorize input or refresh the baseline", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", "原文"); var baseline = Frame(32);
            var state = new VisionInputRecheck(guard, baseline, 96);
            state.Observe(Frame(64));
            var valid = await state.ConfirmAsync(_ => { guard.CheckTitle("另一个人"); return Copied("原文"); },
                _ => throw new Exception("A changed chat was settled."), default);
            if (valid || guard.TryIssuePaste() || !ReferenceEquals(state.Baseline, baseline))
                throw new Exception("Matching text bypassed a title change.");
        });
        yield return ("Clipboard read failure keeps a visual recheck invalid and never refreshes its baseline", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", "原文"); var baseline = Frame(32);
            var state = new VisionInputRecheck(guard, baseline, 96); state.Observe(Frame(64));
            try
            {
                await state.ConfirmAsync(_ => throw new InvalidOperationException("clipboard unreadable"),
                    _ => throw new Exception("An unreadable draft was settled."), default);
                throw new Exception("Clipboard failure was accepted.");
            }
            catch (InvalidOperationException e) when (e.Message == "clipboard unreadable") { }
            if (guard.Valid || guard.TryIssuePaste() || !ReferenceEquals(state.Baseline, baseline))
                throw new Exception("A read failure authorized prefill.");
        });
        yield return ("Cancellation during draft recheck stops before baseline replacement", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            var guard = new OcrPrefillGuard("测试好友", "原文"); var baseline = Frame(32);
            var state = new VisionInputRecheck(guard, baseline, 96); state.Observe(Frame(64));
            try
            {
                await state.ConfirmAsync(_ => { cancellation.Cancel(); return Copied("原文"); },
                    _ => throw new Exception("Cancelled input was settled."), cancellation.Token);
                throw new Exception("Cancellation was ignored.");
            }
            catch (OperationCanceledException) { }
            if (!state.Pending || !ReferenceEquals(state.Baseline, baseline)) throw new Exception("Cancellation refreshed the baseline.");
        });
        yield return ("Activity while settling a verified draft prevents prefill", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", "原文"); var baseline = Frame(32);
            var state = new VisionInputRecheck(guard, baseline, 96); state.Observe(Frame(64));
            try
            {
                await state.ConfirmAsync(_ => Copied("原文"),
                    _ => throw new InvalidOperationException("external activity"), default);
                throw new Exception("Activity while settling was accepted.");
            }
            catch (InvalidOperationException e) when (e.Message == "external activity") { }
            if (guard.Valid || guard.TryIssuePaste() || !ReferenceEquals(state.Baseline, baseline))
                throw new Exception("An unstable input recovered prefill.");
        });
        yield return ("Concurrent input confirmations share a single full draft recheck", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", "原文");
            var state = new VisionInputRecheck(guard, Frame(32), 96); state.Observe(Frame(64));
            var completion = new TaskCompletionSource<ClipboardDraftRead>(TaskCreationOptions.RunContinuationsAsynchronously); var reads = 0;
            Task<ClipboardDraftRead> Read(CancellationToken _) { reads++; return completion.Task; }
            var first = state.ConfirmAsync(Read, _ => Task.FromResult(Frame(64)), default);
            var second = state.ConfirmAsync(Read, _ => Task.FromResult(Frame(64)), default);
            completion.SetResult(new(true, "原文"));
            if (!(await first) || !(await second) || reads != 1) throw new Exception("Concurrent confirmations copied or settled twice.");
        });
        yield return ("An unchanged clipboard cannot prove an empty draft after its input image changed", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", ""); var baseline = Frame(32);
            var state = new VisionInputRecheck(guard, baseline, 96); state.Observe(Frame(64));
            try
            {
                await state.ConfirmAsync(_ => Task.FromResult(new ClipboardDraftRead(false, "")),
                    _ => throw new Exception("An assumed empty draft refreshed its baseline."), default);
                throw new Exception("A failed copy was accepted as verified empty.");
            }
            catch (InvalidOperationException e) when (e.Message.Contains("未取得更新")) { }
            if (guard.Valid || guard.TryIssuePaste() || !ReferenceEquals(state.Baseline, baseline))
                throw new Exception("An assumed empty value recovered prefill after a visual change.");
        });
        yield return ("An actual copied empty text can confirm a visually changed empty draft", async () =>
        {
            var guard = new OcrPrefillGuard("测试好友", "");
            var state = new VisionInputRecheck(guard, Frame(32), 96); state.Observe(Frame(64));
            if (!await state.ConfirmAsync(_ => Copied(""), _ => Task.FromResult(Frame(64)), default) || !guard.Valid || state.Pending)
                throw new Exception("An actual copied empty text was confused with an unchanged clipboard.");
        });
        yield return ("Background visual differences retain task diagnostics without an ambient debug session", async () =>
        {
            var folder = Path.Combine(root, "input-recheck-debug-" + Guid.NewGuid().ToString("N"));
            using var debug = DebugSession.Begin(true, folder, "input-recheck-test");
            var state = new VisionInputRecheck(new("测试好友", "原文"), Frame(32), 192, debug);
            Task observe;
            using (ExecutionContext.SuppressFlow())
                observe = Task.Run(() =>
                {
                    if (DebugSession.Current is not null) throw new Exception("The timer simulation inherited an ambient session.");
                    state.Observe(Frame(64));
                });
            await observe;
            if (!state.Pending) throw new Exception("Background input change was lost.");
            if (DebugSession.IsSupported)
            {
                var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
                if (!files.Any(x => x.EndsWith("input-change.json")) ||
                    !files.Any(x => x.EndsWith("input-baseline.png")) || !files.Any(x => x.EndsWith("input-changed.png")))
                    throw new Exception("The background comparison lost its task artifacts.");
            }
            else if (Directory.Exists(folder)) throw new Exception("Production retained intermediate screenshots.");
        });
    }

    private static Task<ClipboardDraftRead> Copied(string text) => Task.FromResult(new ClipboardDraftRead(true, text));
    private static VisionRegionSnapshot Frame(byte value) => new(8, 8, Enumerable.Repeat(value, 8 * 8 * 4).ToArray());
}
