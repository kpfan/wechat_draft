using System.Collections.Generic;
using System.Linq;

namespace WechatDraft.Windows;

public static class WechatWindowSelection
{
    public static WechatWindow? Select(IReadOnlyList<WechatWindow> windows, WechatWindow? previous)
    {
        if (previous is not null)
        {
            var retained = windows.FirstOrDefault(window => window.Handle == previous.Handle &&
                window.ProcessId == previous.ProcessId && window.ProcessStartTicks == previous.ProcessStartTicks);
            if (retained is not null) return retained;
        }
        return windows.Count == 1 ? windows[0] : null;
    }
}
