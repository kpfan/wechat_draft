using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace WechatDraft.App;

internal static class Ui
{
    public static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16)));
    }
    public static TextBlock Text(string text, double size = 14, string color = "263537", bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = Brush(color), TextWrapping = TextWrapping.Wrap,
        FontWeight = new FontWeight { Weight = (ushort)(bold ? 600 : 400) }, IsTextSelectionEnabled = false
    };
    public static StackPanel Stack(double spacing = 12, params UIElement[] children)
    {
        var stack = new StackPanel { Spacing = spacing };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }
    public static StackPanel Row(double spacing = 10, params UIElement[] children)
    {
        var row = Stack(spacing, children); row.Orientation = Orientation.Horizontal; return row;
    }
    public static Border Card(UIElement child, string background = "FFFFFF", double padding = 24) => new()
    {
        Child = child, Background = Brush(background), Padding = new Thickness(padding), CornerRadius = new CornerRadius(14),
        BorderBrush = Brush("E3E9E7"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch
    };
    public static Border Badge(string text, string color = "197861", string bg = "E7F3ED") => new()
    {
        Child = Text(text, 12, color, true), Background = Brush(bg), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 5, 9, 5),
        HorizontalAlignment = HorizontalAlignment.Left
    };
    public static Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Padding = new Thickness(18, 10, 18, 10), CornerRadius = new CornerRadius(8) };
        if (primary) { button.Background = Brush("16765C"); button.Foreground = Brush("FFFFFF"); button.BorderThickness = new Thickness(0); }
        button.Click += (_, _) => action(); return button;
    }
    public static TextBox Input(string header, string value, string placeholder = "") => new()
    { Header = header, Text = value, PlaceholderText = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(7) };
    public static ComboBox Combo(string header, IEnumerable<string> items, string current)
    {
        var box = new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(7) };
        foreach (var item in items) box.Items.Add(item);
        box.SelectedItem = current; if (box.SelectedIndex < 0) box.SelectedIndex = 0; return box;
    }
}
