using System.Windows;
using System.Windows.Media;

namespace QuickLookNext;

/// <summary>
/// v3.11.0: single source of truth for the custom-drawn surfaces (tray menu
/// and plugin manager panel). Text / fill colors come from one palette so the
/// two surfaces cannot drift apart; the accent prefers the live WPF-UI theme
/// accent so all surfaces follow the system accent color.
/// </summary>
internal static class ThemePalette
{
    // v3.22.0: the palette is fixed per theme, so cache one frozen brush per
    // (color, theme) pair instead of allocating a new unfrozen SolidColorBrush
    // on every tray-menu / plugin-panel construction. Frozen brushes are
    // immutable, thread-safe and cheaper for WPF to use across renders.
    private static readonly Brush LightText = Create("#1A1A1A");
    private static readonly Brush DarkText = Create("#F5F5F5");
    private static readonly Brush LightSecondaryText = Create("#7A7A7A");
    private static readonly Brush DarkSecondaryText = Create("#9E9E9E");
    private static readonly Brush LightHover = Create("#10000000");
    private static readonly Brush DarkHover = Create("#14FFFFFF");
    private static readonly Brush LightSeparator = Create("#10000000");
    private static readonly Brush DarkSeparator = Create("#14FFFFFF");
    private static readonly Brush LightBorder = Create("#26000000");
    private static readonly Brush DarkBorder = Create("#26FFFFFF");
    // v5.0.0: the menu surfaces moved to the Windows 11 host backdrop (see
    // MenuSurface), which is far more transparent than the old double tinted acrylic
    // - the brush alpha had to come down with it, otherwise the material is hidden
    // behind ~75% of flat colour. Measured against the real Windows 11 menu.
    private static readonly Brush LightTint = Create("#38F8F6F4");
    private static readonly Brush DarkTint = Create("#3320242A");
    private static readonly Brush LightButtonBg = Create("#14000000");
    private static readonly Brush DarkButtonBg = Create("#14FFFFFF");
    private static readonly Brush LightButtonHover = Create("#24000000");
    private static readonly Brush DarkButtonHover = Create("#24FFFFFF");
    private static readonly Brush LightDanger = Create("#FFC42B1C");
    private static readonly Brush DarkDanger = Create("#FFFF7B72");
    private static readonly Brush LightScrollbarThumb = Create("#59000000");
    private static readonly Brush DarkScrollbarThumb = Create("#66FFFFFF");
    private static readonly Brush LightAccentFallback = Create("#005FB8");
    private static readonly Brush DarkAccentFallback = Create("#60CDFF");

    internal static Brush Text(bool isDark) => isDark ? DarkText : LightText;

    internal static Brush SecondaryText(bool isDark) => isDark ? DarkSecondaryText : LightSecondaryText;

    internal static Brush Hover(bool isDark) => isDark ? DarkHover : LightHover;

    internal static Brush Separator(bool isDark) => isDark ? DarkSeparator : LightSeparator;

    internal static Brush Border(bool isDark) => isDark ? DarkBorder : LightBorder;

    internal static Brush Tint(bool isDark) => isDark ? DarkTint : LightTint;

    internal static Brush ButtonBg(bool isDark) => isDark ? DarkButtonBg : LightButtonBg;

    internal static Brush ButtonHover(bool isDark) => isDark ? DarkButtonHover : LightButtonHover;

    internal static Brush Danger(bool isDark) => isDark ? DarkDanger : LightDanger;

    /// <summary>
    /// v3.20.0: scrollbar thumb - dark on light surfaces, light on dark ones.
    /// </summary>
    internal static Brush ScrollbarThumb(bool isDark) => isDark ? DarkScrollbarThumb : LightScrollbarThumb;

    internal static Brush Accent(bool isDark)
    {
        return LiveAccent() ?? (isDark ? DarkAccentFallback : LightAccentFallback);
    }

    /// <summary>
    /// The accent color at low opacity, used for badges / selected states.
    /// </summary>
    internal static Brush AccentTint(bool isDark)
    {
        if (LiveAccent() is SolidColorBrush solid)
        {
            var c = solid.Color;
            var brush = new SolidColorBrush(Color.FromArgb(0x18, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        return isDark ? DarkAccentTint : LightAccentTint;
    }

    private static readonly Brush LightAccentTint = Create("#18005FB8");
    private static readonly Brush DarkAccentTint = Create("#1860CDFF");

    private static Brush LiveAccent()
    {
        try
        {
            return Application.Current?.TryFindResource("AccentFillColorDefaultBrush") as Brush;
        }
        catch
        {
            // Theme resources unavailable; fall back to the fixed palette.
            return null;
        }
    }

    private static Brush Create(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
