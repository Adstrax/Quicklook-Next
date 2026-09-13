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
    // The menu surface is WCA acrylic (see MenuSurface) under a double tint: the accent
    // tint at MenuSurface.TintOpacity over a brush at TintBrushAlpha. The two layers
    // multiply, so the surface ends up at
    //     effective opacity = 1 - (1 - accentOpacity) * (1 - brushAlpha)
    // Tuning history while matching the Windows 11 menu look: the pre-5.0.0 acrylic was
    // #8C/#B8 at 30% accent (~70%/80% effective, "milky"), 5.0.0's host backdrop used
    // #38/#33 at 12% accent (~30%, read as fully transparent), then #24/#49/#92/#B8/#DB
    // were measured in turn. A blue-teal base (#0C2A3A at 90%) was tried and dropped - it
    // looked like TranslucentTB's own theme colour rather than this app's neutral material.
    // v5.0.3: settled on ~30% effective (accent 30%, brush 0%), i.e. the composite opacity
    // 5.0.0's host backdrop had, but back on WCA acrylic and on the original neutral
    // colours. Measured with the menu at one fixed spot, surface luminance over the bright
    // wallpaper (L~117) as the opacity went up: 10% -> 113, 20% -> 104, 30% -> 96,
    // 40% -> 87, 60% -> 70, 90% -> ~52 (a dark panel). 30% keeps the wallpaper readable
    // through the panel while the menu still reads as a surface rather than clear glass.
    private const byte TintBrushAlpha = 0x00;
    private static readonly Color LightTintColor = Color.FromRgb(0xF8, 0xF6, 0xF4);
    private static readonly Color DarkTintColor = Color.FromRgb(0x20, 0x24, 0x2A);

    /// <summary>The tint colour the accent policy and the panel brush both paint.</summary>
    internal static Color TintColor(bool isDark) => isDark ? DarkTintColor : LightTintColor;

    private static readonly Brush LightTint = CreateTint(LightTintColor);
    private static readonly Brush DarkTint = CreateTint(DarkTintColor);
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

    private static Brush CreateTint(Color color)
    {
        var brush = new SolidColorBrush(Color.FromArgb(TintBrushAlpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
