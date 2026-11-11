using System;
using WechatDraft.Core;

namespace WechatDraft.Windows;

// Input panel changes are diagnostics. The live draft is read at write time.
internal sealed class VisionInputChangeLog(VisionRegionSnapshot baseline, uint dpi, DebugSession debug)
{
    private bool _recorded;

    public void Observe(VisionRegionSnapshot current)
    {
        if (_recorded || !debug.Enabled || baseline.Matches(current, true, dpi)) return;
        _recorded = true;
        try
        {
            var difference = baseline.Difference(current);
            debug.WriteJson("input-change", new { difference.Pixels, difference.Bounds, dpi, action = "diagnostic-only" });
            debug.WriteBytes("input-baseline", baseline.ToPng(), ".png");
            debug.WriteBytes("input-changed", current.ToPng(), ".png");
        }
        catch (Exception e) { debug.Event("input-change-debug-failed", new { type = e.GetType().Name }); }
    }
}
