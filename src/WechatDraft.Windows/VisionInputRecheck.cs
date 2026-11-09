using System;
using System.Threading;
using System.Threading.Tasks;
using WechatDraft.Core;

namespace WechatDraft.Windows;

// A changed image requests a text check; it is not proof that the draft changed.
internal sealed class VisionInputRecheck(OcrPrefillGuard guard, VisionRegionSnapshot baseline, uint dpi, DebugSession? debug = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public VisionRegionSnapshot Baseline { get; private set; } = baseline;
    public bool Pending { get; private set; }

    public void Observe(VisionRegionSnapshot current)
    {
        if (Baseline.Matches(current, true, dpi) || Pending) return;
        Pending = true;
        // Keep the task's explicit session: timer callbacks need not have its AsyncLocal context.
        if (debug is not { Enabled: true }) return;
        try
        {
            var difference = Baseline.Difference(current);
            debug.WriteJson("input-change", new { difference.Pixels, difference.Bounds, dpi, action = "recheck-draft-before-prefill" });
            debug.WriteBytes("input-baseline", Baseline.ToPng(), ".png");
            debug.WriteBytes("input-changed", current.ToPng(), ".png");
        }
        catch (Exception e) { debug.Event("input-change-debug-failed", new { type = e.GetType().Name }); }
    }

    public async Task<bool> ConfirmAsync(Func<CancellationToken, Task<ClipboardDraftRead>> readDraft,
        Func<CancellationToken, Task<VisionRegionSnapshot>> settleInput, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!guard.Valid) return false;
            if (!Pending) return true;
            debug?.Event("visual-input-recheck", new { stage = "reading-draft" });
            var current = await readDraft(ct);
            ct.ThrowIfCancellationRequested();
            if (!current.ClipboardUpdated)
                throw new InvalidOperationException("输入区画面有变化，但未取得更新的草稿文字，未继续预填。请检查后复制正文。");
            if (!guard.CheckDraft(current.Text))
            {
                debug?.Event("visual-input-recheck", new { matched = false, reason = guard.Reason });
                return false;
            }
            var settled = await settleInput(ct);
            ct.ThrowIfCancellationRequested();
            if (!guard.Valid) return false;
            Baseline = settled;
            Pending = false;
            debug?.Event("visual-input-recheck", new { matched = true, action = "refresh-input-baseline" });
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            guard.Reject(e.Message);
            debug?.Event("visual-input-recheck-failed", new { type = e.GetType().Name, e.Message });
            throw;
        }
        finally { _gate.Release(); }
    }
}
