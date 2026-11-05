using WechatDraft.Windows;

internal static class UiaForegroundChecks
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("UIA browsing the explicitly bound assistant does not invalidate the original chat", () =>
        {
            var state = new VisionInteractionState(stopOnWheel: false, stopOnClick: false, stopOnKeyboard: false);
            state.Foreground(2, 2, 1, companion: 2);
            state.Mouse(0x201, false); state.Keyboard(false);
            state.Foreground(1, 1, 1, companion: 2);
            Equal(false, state.Changed);
            Equal(true, VisionInteractionState.IsAllowedForeground(2, 1, 2));
            Equal(false, VisionInteractionState.IsAllowedForeground(0, 1, 0));
            Equal(false, VisionInteractionState.IsAllowedForeground(2, 1));
            return Task.CompletedTask;
        });
        yield return ("An external foreground switch stays invalid after returning to the assistant or WeChat", () =>
        {
            var state = new VisionInteractionState(stopOnWheel: false, stopOnClick: false, stopOnKeyboard: false);
            state.Foreground(3, 3, 1, companion: 2);
            state.Foreground(2, 2, 1, companion: 2); state.Foreground(1, 1, 1, companion: 2);
            Equal(true, state.Changed); Equal("foreground-change", state.Reason);
            var strict = new VisionInteractionState(); strict.Foreground(2, 2, 1);
            Equal(true, strict.Changed);
            return Task.CompletedTask;
        });
        yield return ("Foreground readiness waits without activating any window and rechecks before resuming", async () =>
        {
            var foreground = false; var checks = 0; var notices = 0;
            var pending = UiaForegroundWaiter.WaitAsync(() => foreground, _ =>
            {
                checks++;
                if (checks == 3) foreground = true;
                return Task.FromResult(true);
            }, () => notices++, default, interval: TimeSpan.FromMilliseconds(1));
            Equal(ForegroundWaitResult.Ready, await pending);
            Equal(3, checks); Equal(1, notices);
        });
        yield return ("A changed chat is refused even when the original WeChat window returns", async () =>
        {
            var foreground = false; var valid = true;
            var result = await UiaForegroundWaiter.WaitAsync(() => foreground, _ => Task.FromResult(valid),
                () => { valid = false; foreground = true; }, default, interval: TimeSpan.FromMilliseconds(1));
            Equal(ForegroundWaitResult.Changed, result);
        });
        yield return ("Foreground waiting times out without writing or silently continuing", async () =>
        {
            var result = await UiaForegroundWaiter.WaitAsync(() => false, _ => Task.FromResult(true),
                () => throw new Exception("Already timed out"), default, timeout: TimeSpan.Zero);
            Equal(ForegroundWaitResult.TimedOut, result);
        });
        yield return ("Foreground waiting supports cancellation and never resumes afterward", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            try
            {
                await UiaForegroundWaiter.WaitAsync(() => false, _ => Task.FromResult(true), cancellation.Cancel,
                    cancellation.Token, interval: TimeSpan.FromMilliseconds(1));
                throw new Exception("Cancelled waiting resumed");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
    }

    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
}
