using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WechatDraft.Windows;

internal enum ForegroundWaitResult { Ready, Changed, TimedOut }

internal static class UiaForegroundWaiter
{
    internal static async Task<ForegroundWaitResult> WaitAsync(Func<bool> isTargetForeground,
        Func<CancellationToken, Task<bool>> isUnchanged, Action waiting, CancellationToken cancellationToken,
        TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var started = Stopwatch.StartNew();
        var notified = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await isUnchanged(cancellationToken)) return ForegroundWaitResult.Changed;
            if (isTargetForeground()) return ForegroundWaitResult.Ready;
            if (started.Elapsed >= (timeout ?? TimeSpan.FromSeconds(30))) return ForegroundWaitResult.TimedOut;
            if (!notified) { notified = true; waiting(); }
            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }
}
