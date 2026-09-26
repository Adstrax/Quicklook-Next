// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.3.0: the button of every hand-drawn panel - update prompt, download panel, data &amp; cache,
/// OCR - in one place. It used to be four copies of the same twenty lines, which is exactly how
/// they ended up with three different hover treatments and no keyboard focus state at all.
/// </summary>
internal static class PanelStyles
{
    /// <summary>
    /// Flat, rounded button with hover, press and a keyboard focus ring. The stock WPF template
    /// paints a grey gradient that does not belong on these surfaces.
    /// </summary>
    internal static ControlTemplate ButtonTemplate(Brush hover)
    {
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover) { TargetName = "bd" });
        template.Triggers.Add(hoverTrigger);

        var pressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.8d) { TargetName = "bd" });
        template.Triggers.Add(pressedTrigger);

        // v5.3.0: the panels are reachable with the keyboard, so focus has to be visible.
        var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, FocusBrush()) { TargetName = "bd" });
        template.Triggers.Add(focusTrigger);

        return template;
    }

    /// <summary>The accent used for focus rings, with a fallback when the theme resource is missing.</summary>
    internal static Brush FocusBrush()
    {
        try
        {
            return Application.Current?.TryFindResource("AccentFillColorDefaultBrush") as Brush
                   ?? Brushes.DodgerBlue;
        }
        catch
        {
            return Brushes.DodgerBlue;
        }
    }

    internal static Brush WithOpacity(Brush brush, double opacity)
    {
        if (brush is not SolidColorBrush solid)
            return brush;

        var faded = new SolidColorBrush(solid.Color) { Opacity = opacity };
        faded.Freeze();
        return faded;
    }
}
