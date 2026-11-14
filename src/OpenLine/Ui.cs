using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OpenLine;

/// <summary>Small helpers callable from x:Bind.</summary>
public static class Ui
{
    private static readonly Dictionary<string, SolidColorBrush> Solid = [];
    private static readonly Dictionary<string, SolidColorBrush> Soft = [];

    public static SolidColorBrush Brush(string hex)
    {
        if (!Solid.TryGetValue(hex, out var b)) Solid[hex] = b = new SolidColorBrush(Parse(hex, 0xFF));
        return b;
    }

    /// <summary>Same hue at low alpha, for icon tiles.</summary>
    public static SolidColorBrush SoftBrush(string hex)
    {
        if (!Soft.TryGetValue(hex, out var b)) Soft[hex] = b = new SolidColorBrush(Parse(hex, 0x2E));
        return b;
    }

    public static Visibility ShowIf(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    private static Color Parse(string hex, byte alpha)
    {
        var s = hex.TrimStart('#');
        return Color.FromArgb(alpha,
            Convert.ToByte(s[..2], 16),
            Convert.ToByte(s[2..4], 16),
            Convert.ToByte(s[4..6], 16));
    }
}
