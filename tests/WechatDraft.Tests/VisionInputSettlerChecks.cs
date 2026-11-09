using WechatDraft.Windows;

internal static class VisionInputSettlerChecks
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("The input baseline is captured after a transient selection has collapsed", async () =>
        {
            var selected = Frame(60); var collapsed = Frame(200); var observations = 0;
            var result = await VisionInputSettler.WaitAsync(_ => Task.FromResult(++observations == 1 ? selected : collapsed),
                () => { }, 96, default, timeout: TimeSpan.FromSeconds(1), interval: TimeSpan.FromMilliseconds(1));
            if (observations != 3 || !result.Matches(collapsed, false)) throw new Exception("A transient selection became the baseline.");
        });
        yield return ("Readback begins only after the pending paste has consumed its clipboard and painted", async () =>
        {
            var before = Frame(60); var after = Frame(200); var observations = 0;
            var clipboard = "suffix"; var consumed = false;
            await VisionInputSettler.WaitAsync(_ =>
            {
                if (++observations <= 2) return Task.FromResult(before);
                consumed = clipboard == "suffix";
                return Task.FromResult(after);
            }, () => { }, 96, default, before, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
            clipboard = "read-probe";
            if (!consumed || observations != 4) throw new Exception("Readback replaced a pending paste.");
        });
        yield return ("An unchanged post-paste frame times out without requesting another paste", async () =>
        {
            var frame = Frame(60);
            try
            {
                await VisionInputSettler.WaitAsync(_ => Task.FromResult(frame), () => { }, 96, default, frame,
                    TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1));
                throw new Exception("An unchanged input proved a paste had finished.");
            }
            catch (InvalidOperationException e) when (e.Message.Contains("尚未确认")) { }
        });
        yield return ("A transient post-paste change returning to the old image does not authorize readback", async () =>
        {
            var before = Frame(60); var transient = Frame(200); var observations = 0;
            try
            {
                await VisionInputSettler.WaitAsync(_ => Task.FromResult(++observations == 2 ? transient : before),
                    () => { }, 96, default, before, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1));
                throw new Exception("A reverted transient change authorized readback.");
            }
            catch (InvalidOperationException e) when (e.Message.Contains("尚未确认")) { }
        });
        yield return ("Cancellation and external activity stop input stabilization", async () =>
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try
            {
                await VisionInputSettler.WaitAsync(_ => throw new Exception("observed after cancellation"), () => { }, 96, cancelled.Token);
                throw new Exception("Cancelled stabilization continued.");
            }
            catch (OperationCanceledException) { }
            try
            {
                await VisionInputSettler.WaitAsync(_ => Task.FromResult(Frame(60)),
                    () => throw new InvalidOperationException("external input"), 96, default);
                throw new Exception("External activity was ignored.");
            }
            catch (InvalidOperationException e) when (e.Message == "external input") { }
        });
    }

    private static VisionRegionSnapshot Frame(byte value) => new(8, 8, Enumerable.Repeat(value, 8 * 8 * 4).ToArray());
}
