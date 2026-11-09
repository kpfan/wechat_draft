using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WechatDraft.Core;

namespace WechatDraft.Windows;

internal static class VisionInputSettler
{
    internal static async Task<VisionRegionSnapshot> WaitAsync(Func<CancellationToken, Task<VisionRegionSnapshot>> observe,
        Action checkActivity, uint dpi, CancellationToken ct, VisionRegionSnapshot? changedFrom = null,
        TimeSpan? timeout = null, TimeSpan? interval = null)
    {
        var timer = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromMilliseconds(500);
        VisionRegionSnapshot? previous = null;
        var observations = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested(); checkActivity();
            var current = await observe(ct);
            checkActivity(); observations++;
            var changed = changedFrom is null || !changedFrom.Matches(current, true, dpi);
            if (changed && previous is not null && previous.Matches(current, true, dpi))
            {
                DebugSession.Current?.Event("visual-input-settled", new { phase = changedFrom is null ? "after-copy" : "after-paste",
                    observations, elapsedMs = timer.Elapsed.TotalMilliseconds });
                return current;
            }
            var remaining = limit - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new InvalidOperationException(changedFrom is null
                ? "等待微信收起选区并稳定输入画面超时，未继续预填。"
                : "尚未确认微信完成粘贴，未发起读回或重复写入。请检查微信。");
            previous = current;
            var pause = interval ?? TimeSpan.FromMilliseconds(10);
            await Task.Delay(pause < remaining ? pause : remaining, ct);
        }
    }
}
