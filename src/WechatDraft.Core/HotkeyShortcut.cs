namespace WechatDraft.Core;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

public readonly record struct HotkeyShortcut(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private const HotkeyModifiers AllModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows;
    private const uint FirstFunctionKey = 0x70;
    private const uint LastFunctionKey = 0x7A; // F12 is reserved by Windows.

    private static bool IsLetterOrDigit(uint key) => key is >= 'A' and <= 'Z' or >= '0' and <= '9';

    public bool IsValid => (Modifiers & ~AllModifiers) == 0 &&
        (VirtualKey is >= FirstFunctionKey and <= LastFunctionKey ||
            (Modifiers != HotkeyModifiers.None && IsLetterOrDigit(VirtualKey)));

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(VirtualKey is >= FirstFunctionKey and <= LastFunctionKey ? $"F{VirtualKey - FirstFunctionKey + 1}" :
            IsLetterOrDigit(VirtualKey) ? ((char)VirtualKey).ToString() : $"0x{VirtualKey:X}");
        return string.Join(" + ", parts);
    }
}
