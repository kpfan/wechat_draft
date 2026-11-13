using WechatDraft.Windows;

internal static class VisionClipboardPollerChecks
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("An already updated clipboard is read immediately without a fixed delay", () =>
        {
            var inspected = false;
            var result = VisionClipboardPoller.ReadAsync(() => { inspected = true; return (true, "原有草稿"); }, () => { }, default);
            if (!inspected || !result.IsCompletedSuccessfully || result.Result.Text != "原有草稿")
                throw new Exception("Clipboard inspection waited instead of checking current state immediately.");
            return Task.CompletedTask;
        });
        yield return ("Delayed clipboard updates are read before assuming an empty draft", async () =>
        {
            var attempts = 0;
            var read = await VisionClipboardPoller.ReadAsync(() => ++attempts < 3 ? (false, (string?)null) : (true, "实际原文"),
                () => { }, default, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
            if (!read.ClipboardUpdated || read.Text != "实际原文" || read.Attempts != 3)
                throw new Exception("The pending copy was mistaken for an empty input.");
        });
        yield return ("A temporarily busy updated clipboard can finish without issuing another copy", async () =>
        {
            var attempts = 0;
            var read = await VisionClipboardPoller.ReadAsync(() => ++attempts == 1
                    ? throw new ClipboardBusyException("busy") : (true, "原文"), () => { }, default,
                TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
            if (!read.ClipboardUpdated || read.Text != "原文") throw new Exception("A busy clipboard became an empty input.");
        });
        yield return ("A busy clipboard timeout remains an error rather than an empty draft", async () =>
        {
            try
            {
                await VisionClipboardPoller.ReadAsync(() => throw new ClipboardBusyException("busy"), () => { }, default,
                    TimeSpan.Zero);
                throw new Exception("Busy clipboard timeout became empty.");
            }
            catch (ClipboardBusyException) { }
        });
        yield return ("Updated clipboard text preserves the complete draft including whitespace", async () =>
        {
            foreach (var text in new[] { " 原有草稿\r\n", " \r\n", "" })
            {
                var read = await VisionClipboardPoller.ReadAsync(() => (true, text), () => { }, default);
                if (!read.ClipboardUpdated || read.Text != text) throw new Exception("A new copy was normalized or treated as an unchanged clipboard.");
            }
        });
        yield return ("Unchanged clipboard is explicitly assumed empty without returning the probe or stale text", async () =>
        {
            var read = await VisionClipboardPoller.ReadAsync(() => (false, "stale clipboard"), () => { }, default);
            if (read.ClipboardUpdated || read.Text != "") throw new Exception("Unchanged clipboard did not follow the user-selected empty rule.");
        });
        yield return ("Clipboard polling stops on cancellation or activity changes before accepting text", async () =>
        {
            using var cancellation = new CancellationTokenSource();
            try
            {
                cancellation.Cancel();
                await VisionClipboardPoller.ReadAsync(() => throw new Exception("Copy read after cancellation"),
                    () => { }, cancellation.Token);
                throw new Exception("Cancelled copy continued.");
            }
            catch (OperationCanceledException) { }
            var read = false;
            try
            {
                await VisionClipboardPoller.ReadAsync(() => { read = true; return (true, "draft"); },
                    () => throw new InvalidOperationException("changed"), default);
                throw new Exception("Changed input was accepted.");
            }
            catch (InvalidOperationException e) when (e.Message == "changed") { }
            if (read) throw new Exception("Clipboard was read after input changed.");
        });
        yield return ("A failed copy is not an empty result when the clipboard reports an error", async () =>
        {
            try
            {
                await VisionClipboardPoller.ReadAsync(() => throw new InvalidOperationException("non-text or busy"), () => { }, default);
                throw new Exception("Copy error was treated as empty.");
            }
            catch (InvalidOperationException e) when (e.Message == "non-text or busy") { }
        });
    }
}
