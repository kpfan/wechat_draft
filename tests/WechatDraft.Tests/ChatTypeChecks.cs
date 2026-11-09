using WechatDraft.Core;
using WechatDraft.Windows;

internal static class ChatTypeChecks
{
    private const string SingleClasses = "mmui::ChatTitleBarMasterView mmui::ChatSingleTitleBarView mmui::XTextView";

    private static WechatConversationEvidence Private(string name = "张三") =>
        new(name, HasInput: true, HasTitleBar: true, IsBrandPage: false, SingleClasses, MemberCount: null,
            HasGroupAffordance: false, HasSingleChatAffordance: true);

    // 任何非私聊证据都必须以普通 InvalidOperationException 停止；
    // 若抛出 UiAutomationUnavailableException，会回退到无法核实会话类型的视觉路径。
    private static string Reject(WechatConversationEvidence evidence)
    {
        try { WechatConversationRules.RequirePrivateChat(evidence); }
        catch (InvalidOperationException e) when (e is not UiAutomationUnavailableException) { return e.Message; }
        throw new Exception("非私聊证据未被拒绝，或错误地触发了视觉回退。");
    }

    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("UIA private chat with single-chat affordance is the only accepted conversation", () =>
        {
            WechatConversationRules.RequirePrivateChat(Private());
            Equal(WechatConversationKind.Private, WechatConversationRules.Classify(Private()).Kind);
            // 企业联系人等带其他类名的单聊视图同样接受，不按名称猜测关系。
            WechatConversationRules.RequirePrivateChat(Private("某企业-陈老师") with
            { TitleBarClassNames = SingleClasses + " mmui::EnterpriseBadgeView" });
        });
        yield return ("UIA group chats are rejected by view class, member count or group affordance", () =>
        {
            Equal(WechatConversationKind.Group, RejectKind(Private("项目群") with
            { TitleBarClassNames = "mmui::ChatTitleBarMasterView mmui::ChatRoomTitleBarView" }));
            Equal(WechatConversationKind.Group, RejectKind(Private("项目群") with { MemberCount = 35 }));
            // 即使类名是单聊视图，独立成员数控件或群聊入口也优先判定为群。
            Equal(WechatConversationKind.Group, RejectKind(Private() with { MemberCount = 2 }));
            True(Reject(Private() with { HasGroupAffordance = true }).Contains("群聊"));
            True(Reject(Private("项目群(35)")).Contains("私聊"));
        });
        yield return ("UIA official accounts, broadcasts and service notices are not personal chats", () =>
        {
            Equal(WechatConversationKind.Official, RejectKind(Private("某服务号") with
            { TitleBarClassNames = "mmui::ChatTitleBarMasterView mmui::ChatBrandTitleBarView" }));
            Equal(WechatConversationKind.Official, RejectKind(Private("某公众号") with
            { TitleBarClassNames = "mmui::ChatTitleBarMasterView mmui::OfficialTitleView" }));
            Equal(WechatConversationKind.Broadcast, RejectKind(Private("某服务号") with
            { TitleBarClassNames = "mmui::ChatBrandTitleBarView", HasInput = false }));
            Equal(WechatConversationKind.Notice, RejectKind(Private("服务通知") with { HasInput = false }));
            Equal(WechatConversationKind.Feed, RejectKind(Private() with { IsBrandPage = true }));
        });
        yield return ("UIA functional and system accounts are rejected by exact name only", () =>
        {
            foreach (var name in new[] { "文件传输助手", "服务通知", "微信支付", "微信团队", "QQ邮箱提醒", "腾讯新闻", "订阅号消息" })
                True(Reject(Private(name)).Length > 0, name + " must not be treated as a personal chat");
            // 普通备注中包含这些词不应被精确匹配误伤。
            WechatConversationRules.RequirePrivateChat(Private("微信支付对账-财务陈老师"));
            WechatConversationRules.RequirePrivateChat(Private("腾讯新闻爱好者小李"));
        });
        yield return ("UIA unknown or missing title bar evidence stops instead of guessing", () =>
        {
            Equal(WechatConversationKind.Unknown, RejectKind(Private() with { HasTitleBar = false, TitleBarClassNames = "" }));
            Equal(WechatConversationKind.Unknown, RejectKind(Private() with { TitleBarClassNames = "mmui::XHBoxView" }));
            Equal(WechatConversationKind.Unknown, RejectKind(Private() with { TitleBarClassNames = null! }));
        });
        yield return ("UIA private chat without a single-chat affordance never falls back to vision", () =>
        {
            var message = Reject(Private() with { HasSingleChatAffordance = false });
            True(message.Contains("聊天信息"), "missing affordance must be explained");
        });
        yield return ("UIA title suffix uses counts above two while independent member-count controls stay authoritative", () =>
        {
            WechatConversationRules.RequirePrivateChat(Private("(1)"));
            WechatConversationRules.RequirePrivateChat(Private("联系人(2)"));
            WechatConversationRules.RequirePrivateChat(Private("联系人(35)-备注"));
            True(Reject(Private("联系人（3）")).Length > 0, "group-count suffix must stop");
            Equal(WechatConversationKind.Group, RejectKind(Private("(1)") with { MemberCount = 1 }));
            // 群成员数解析只接受完整括号数字叶文本。
            True(WechatConversationRules.MemberCountLeaf.IsMatch("（35）"));
            True(WechatConversationRules.MemberCountLeaf.IsMatch("( 35 )"));
            True(!WechatConversationRules.MemberCountLeaf.IsMatch("项目群(35)"));
            True(!WechatConversationRules.MemberCountLeaf.IsMatch("35"));
        });
    }

    private static WechatConversationKind RejectKind(WechatConversationEvidence evidence)
    {
        Reject(evidence);
        return WechatConversationRules.Classify(evidence).Kind;
    }

    private static void True(bool condition, string message = "condition failed")
    { if (!condition) throw new Exception(message); }
}
