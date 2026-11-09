using System.Text;

namespace WechatDraft.Core;

public sealed record GreetingGenerationContext(IReadOnlyList<string> RecentGreetings, string? RejectedCandidate = null);

public sealed class RepeatedGreetingException() : Exception("生成的祝福与已有文案过于相似，本次未预填。请补充更具体的要求后重试。");

public static class GreetingDiversity
{
    public static string Normalize(string text, string? address = null)
    {
        static string Letters(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).EnumerateRunes()
            .Where(Rune.IsLetterOrDigit).Select(r => Rune.ToLowerInvariant(r).ToString()));
        var normalized = Letters(text);
        var salutation = string.IsNullOrWhiteSpace(address) ? "" : Letters(address);
        return salutation.Length > 0 && normalized.StartsWith(salutation, StringComparison.Ordinal)
            ? normalized[salutation.Length..] : normalized;
    }

    public static bool TooSimilar(string candidate, string previous, string? address = null)
    {
        var a = Normalize(candidate, address); var b = Normalize(previous, address);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        if (Math.Min(a.Length, b.Length) < 20 || Math.Max(a.Length, b.Length) > 512) return false;
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0]; row[0] = i;
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
