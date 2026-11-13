using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WechatDraft.Windows;

internal sealed record ClipboardDraftRead(bool ClipboardUpdated, string Text, int Attempts = 1, double ElapsedMs = 0)
{
    public bool ConfirmsSuffix(string greeting) =>
        ClipboardUpdated && greeting.Length > 0 && Text.EndsWith(greeting, StringComparison.Ordinal);
}

internal static class VisionClipboardPoller
{
    internal static async Task<ClipboardDraftRead> ReadAsync(Func<(bool Copied, string? Text)> tryRead,
        Action checkActivity, CancellationToken ct, TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var timer = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromMilliseconds(150);
        var attempts = 0;
        ClipboardBusyException? busy = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            checkActivity();
            attempts++;
            try
            {
                var result = tryRead();
                ct.ThrowIfCancellationRequested(); checkActivity();
                if (result.Copied) return new(true,
                    result.Text ?? throw new InvalidOperationException("剪贴板已更新，但没有取得普通文字。"), attempts, timer.Elapsed.TotalMilliseconds);
            }
            catch (ClipboardBusyException e) { busy = e; }
            var remaining = limit - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                if (busy is not null) throw busy;
                // Only an unchanged clipboard after the response window is assumed empty.
                return new(false, "", attempts, timer.Elapsed.TotalMilliseconds);
            }
            var pause = interval ?? TimeSpan.FromMilliseconds(10);
            await Task.Delay(pause < remaining ? pause : remaining, ct);
        }
    }
}
