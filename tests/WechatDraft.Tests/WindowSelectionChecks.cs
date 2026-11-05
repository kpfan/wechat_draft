using WechatDraft.Windows;

internal static class WindowSelectionChecks
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Window discovery selects a sole target but never guesses among multiple windows", () =>
        {
            var first = new WechatWindow(10, "微信", "Weixin", 100, 1000);
            var second = new WechatWindow(20, "微信", "Weixin", 100, 1000);
            Equal(null, WechatWindowSelection.Select([], null));
            Equal(first, WechatWindowSelection.Select([first], null));
            Equal(null, WechatWindowSelection.Select([first, second], null));
        });
        yield return ("Automatic refresh preserves the chosen window through title and list-order changes", () =>
        {
            var chosen = new WechatWindow(10, "旧标题", "Weixin", 100, 1000);
            var renamed = chosen with { Title = "新标题" };
            var other = new WechatWindow(20, "新标题", "Weixin", 100, 1000);
            if (!ReferenceEquals(renamed, WechatWindowSelection.Select([other, renamed], chosen)))
                throw new Exception("A background refresh changed the chosen target.");
        });
        yield return ("Disappeared or recycled window identities do not retain an obsolete selection", () =>
        {
            var old = new WechatWindow(10, "微信", "Weixin", 100, 1000);
            var recycled = old with { ProcessStartTicks = 2000 };
            var other = new WechatWindow(20, "微信", "Weixin", 200, 2000);
            Equal(null, WechatWindowSelection.Select([recycled, other], old));
            Equal(null, WechatWindowSelection.Select([], old));
            Equal(other, WechatWindowSelection.Select([other], old));
            Equal(null, WechatWindowSelection.Select([old with { ProcessId = 300 }, other], old));
        });
    }

    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
}
