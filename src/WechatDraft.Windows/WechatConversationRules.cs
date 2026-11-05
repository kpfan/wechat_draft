using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace WechatDraft.Windows;

public enum WechatConversationKind { Private, Group, Official, Broadcast, Notice, Feed, Unknown }

// UIA 观察结果，与控件读取分离，便于用合成证据验证会话类型决策。
public sealed record WechatConversationEvidence(
    string ChatName,
    bool HasInput,
    bool HasTitleBar,
    bool IsBrandPage,
    string TitleBarClassNames,
    int? MemberCount,
    bool HasGroupAffordance,
    bool HasSingleChatAffordance);

public static class WechatConversationRules
{
    // 微信 4.x Qt 标题栏类名特征，与只读参考实现（wechat-uia-reader/uia_utils.py）一致。
    public const string TitleBarClassSuffix = "ChatTitleBarMasterView";
    public const string BrandPageClass = "mmui::ChatBrandDisorderPage";
    private const string GroupTitleFeature = "ChatRoom";
    private const string SingleTitleFeature = "ChatSingle";
    private static readonly string[] BrandTitleFeatures = ["ChatBrand", "Official"];
    private static readonly Regex MemberCountInTitle = new(@"[（(]\d+[）)]", RegexOptions.Compiled);
    public static readonly Regex MemberCountLeaf = new(@"^[（(]\s*(\d+)\s*[)）]$", RegexOptions.Compiled);
    // 功能/系统账号不是“某个人”；精确匹配，避免误伤包含这些词的普通备注。
    private static readonly string[] NonPersonalNames =
        ["服务通知", "微信支付", "微信运动", "微信团队", "微信安全中心", "QQ邮箱提醒", "腾讯新闻", "订阅号消息"];

    public static (WechatConversationKind Kind, string Reason) Classify(WechatConversationEvidence evidence)
    {
        if (evidence.IsBrandPage) return (WechatConversationKind.Feed, "公众号消息聚合页");
        if (!evidence.HasTitleBar) return (WechatConversationKind.Unknown, "未定位到聊天标题栏容器");
        if (evidence.MemberCount is int count) return (WechatConversationKind.Group, $"标题栏显示群成员数 ({count})");
        var classes = evidence.TitleBarClassNames ?? "";
        if (classes.Contains(GroupTitleFeature, StringComparison.Ordinal))
            return (WechatConversationKind.Group, "标题栏为群聊视图");
        if (BrandTitleFeatures.Any(feature => classes.Contains(feature, StringComparison.Ordinal)))
            return evidence.HasInput
                ? (WechatConversationKind.Official, "公众号/服务号会话视图")
                : (WechatConversationKind.Broadcast, "公众号/服务号推送视图，无输入框");
        if (classes.Contains(SingleTitleFeature, StringComparison.Ordinal))
            return evidence.HasInput
                ? (WechatConversationKind.Private, "标题栏为单聊视图且有输入框")
                : (WechatConversationKind.Notice, "单聊样式但没有输入框（服务通知等）");
        return (WechatConversationKind.Unknown, "标题栏缺少可识别的会话类型特征");
    }

    // 只有证据充分的真人私聊才允许祝福任务。其余一律 InvalidOperationException 停止，
    // 不使用 UiAutomationUnavailableException：控件树已可读时不能回退到无法核实会话类型的视觉路径。
    public static void RequirePrivateChat(WechatConversationEvidence evidence)
    {
        var name = evidence.ChatName ?? "";
        if (name.Contains("文件传输助手", StringComparison.Ordinal) || name.Contains("公众号", StringComparison.Ordinal) ||
            NonPersonalNames.Contains(name.Trim()) || MemberCountInTitle.IsMatch(name))
            throw new InvalidOperationException("当前会话不是与某个人的私聊，已停止；未调用模型。");
        if (evidence.HasGroupAffordance)
            throw new InvalidOperationException("检测到群聊信息入口，不能确认当前为私聊，已停止自动处理。");
        var (kind, reason) = Classify(evidence);
        if (kind != WechatConversationKind.Private)
            throw new InvalidOperationException($"当前会话不是受支持的私聊（{reason}），已停止；未调用模型。");
        // 保留原有的显式单聊入口作为第二重证据；缺失时停止而不是转入视觉路径。
        if (!evidence.HasSingleChatAffordance)
            throw new InvalidOperationException("UIA 未提供单聊“聊天信息”入口，无法确认当前为私聊，已停止；未调用模型。");
    }
}
