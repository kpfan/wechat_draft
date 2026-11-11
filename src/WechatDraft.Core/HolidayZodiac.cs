namespace WechatDraft.Core;

public static class HolidayZodiac
{
    public static IReadOnlyList<string> Years { get; } = Array.AsReadOnly(
        new[] { "鼠年", "牛年", "虎年", "兔年", "龙年", "蛇年", "马年", "羊年", "猴年", "鸡年", "狗年", "猪年" });

    public static bool AppliesTo(string scene) => scene.Trim() is "春节" or "元旦";

    // The lunar year that begins in Gregorian year 2020 is a Rat year.
    public static string ForDate(DateTime date) => Years[(date.Year + 8) % Years.Count];

    public static string ResolveYear(string? year) => year is not null && Years.Contains(year) ? year : ForDate(DateTime.Today);

    public static string? Resolve(AppSettings settings) => AppliesTo(settings.Scene) ? ResolveYear(settings.ZodiacYear) : null;
}
