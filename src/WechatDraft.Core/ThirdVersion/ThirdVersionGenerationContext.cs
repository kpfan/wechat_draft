using System.Text;

namespace WechatDraft.Core.ThirdVersion;

/// <summary>仅供第三版提示词避重复的历史数据，不表示身份依据或写入权限。</summary>
public sealed record GreetingGenerationContext(IReadOnlyList<string> RecentGreetings, string? RejectedCandidate = null);

// 上游已删除全局场景硬校验；第三版保持其原有输出规则，不影响上游兼容协调器。
internal static class ThirdVersionValidation
{
    internal static void ValidateSceneGreeting(string greeting, string scene)
    {
        string[]? markers = scene.Trim() switch
        {
            "春节" => ["春节", "新春", "新年", "过年"],
            "元旦" => ["元旦", "新年"],
            "生日" => ["生日", "生辰", "新的一岁"],
            "中秋" => ["中秋", "月圆", "团圆"],
            "端午" => ["端午"], "国庆" => ["国庆"],
            "七夕" => ["七夕"], "圣诞" => ["圣诞"],
            "纪念日" => ["纪念", "周年"],
            _ => null
        };
        if (markers is not null && !markers.Any(greeting.Contains))
            throw new InvalidDataException("祝福没有体现所选场景，未写入微信。");
    }
}

// 上游已删除全局 diversity 功能；保留第三版原有候选校验，不改变上游模型或协调器行为。
internal static class GreetingDiversity
{
    internal static string Normalize(string text, string? address = null)
    {
        static string Letters(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).EnumerateRunes()
            .Where(Rune.IsLetterOrDigit).Select(r => Rune.ToLowerInvariant(r).ToString()));
        var normalized = Letters(text);
        var salutation = string.IsNullOrWhiteSpace(address) ? "" : Letters(address);
        return salutation.Length > 0 && normalized.StartsWith(salutation, StringComparison.Ordinal)
            ? normalized[salutation.Length..] : normalized;
    }

    internal static bool TooSimilar(string candidate, string previous, string? address = null)
    {
        var a = Normalize(candidate, address);
        var b = Normalize(previous, address);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        if (Math.Min(a.Length, b.Length) < 20 || Math.Max(a.Length, b.Length) > 512) return false;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var above = row[j];
                row[j] = Math.Min(Math.Min(above + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = above;
            }
        }
        return 1 - row[b.Length] / (double)Math.Max(a.Length, b.Length) >= .75;
    }
}
