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

using QuickLook.Common.Helpers;
using QuickLookNext.Helpers;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLookNext;

/// <summary>
/// v3.35.0: the update prompt. Clicking a "new version" notification (or running
/// a manual update check) used to start the download straight away; the user now
/// chooses between updating now and skipping this version.
/// <para>
/// v3.40.0: the prompt is no longer a stock window. It is a borderless panel that
/// reuses the tray menu's material - the same WCA acrylic, tint, border, corner
/// radius and palette - so the update asks the same way the rest of the app looks.
/// The layout is a header row (icon, title, close), the version summary, the
/// release notes in a scrollable card, and a footer with the GitHub link plus
/// the two answers.
/// </para>
/// </summary>
internal sealed class UpdateDialog : Window
{
    private readonly bool _isDark;
    private readonly UpdateReleaseInfo _release;
    private bool _accentApplied;
    private bool _updateNow;

    private UpdateDialog(UpdateReleaseInfo release)
    {
        _release = release;
        _isDark = TrayIconManager.IsDarkTheme();

        Title = TranslationHelper.Get("Update_Title", failsafe: "Software Update");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        Width = Math.Max(360, Math.Min(680, SystemParameters.WorkArea.Width - 48));
        Height = Math.Max(400, Math.Min(620, SystemParameters.WorkArea.Height - 48));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));
        FontSize = 13;
        Foreground = ThemePalette.Text(_isDark);
        // v5.3.0: a dialog is read, not scanned - it uses the firmer panel surface (see MenuSurface).
        Background = MenuSurface.SurfaceBrush(_isDark, MenuSurface.SurfaceProminence.Panel);

        Content = BuildContent(release);

        // Borderless window: the panel itself is the title bar.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // The button was released before the drag started.
                }
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
            else if (e.Key == Key.Enter)
            {
                _updateNow = true;
                Close();
            }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Re-apply after the first paint, like the tray menu and the plugin manager
        // do - some windows only accept the attribute once they are visible.
        ApplyBackdrop();
    }

    /// <summary>Must be called on the UI thread. Returns true to update now.</summary>
    internal static bool Ask(UpdateReleaseInfo release)
    {
        var dialog = new UpdateDialog(release);
        dialog.RunSmokeTestHook();
        dialog.ShowDialog();
        return dialog._updateNow;
    }

    /// <summary>
    /// v3.40.0 test hook: when the smoke test asked for the prompt, close it by itself
    /// (answering "ignore") and record what it looked like, so test.ps1 can assert the
    /// material and the layout without clicking.
    /// <para>
    /// v5.0.11: this used to arm itself whenever <c>App.SmokeDir</c> was set - and that
    /// property is never empty (it falls back to <c>%TEMP%\ql-smoke</c>), so the timer
    /// closed the real prompt two seconds after it appeared, answering "ignore" on the
    /// user's behalf. The hook is now tied to the /test-update-prompt switch that runs
    /// it, and to nothing else.
    /// </para>
    /// </summary>
    private void RunSmokeTestHook()
    {
        if (!App.IsUpdatePromptTestEnabled)
            return;

        var smokeDir = App.SmokeDir;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2000) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            try
            {
                System.IO.Directory.CreateDirectory(smokeDir);
                System.IO.File.WriteAllText(System.IO.Path.Combine(smokeDir, "update-dialog.txt"),
                    $"title={Title}\nbackdrop={DiagnoseBackdrop()}\n" +
                    $"size={ActualWidth:0}x{ActualHeight:0}\nbuttons={DiagnoseButtons()}\n" +
                    $"notes={_release.Notes.Length}\n" +
                    // v5.0.1: the smoke test asserts the dialog follows the UI language
                    // (it used to fall back to the Chinese failsafe on an English UI).
                    $"language={CultureInfo.CurrentUICulture.Name}\n");
            }
            catch
            {
                // Diagnostics must never affect the prompt.
            }

            Close();
        };

        timer.Start();
    }

    internal string DiagnoseBackdrop() => $"accent-applied={_accentApplied} {Helpers.MenuSurface.Diagnose()}";

    private string _buttonLabels = string.Empty;

    internal string DiagnoseButtons() => _buttonLabels;

    private FrameworkElement BuildContent(UpdateReleaseInfo release)
    {
        var accent = ThemePalette.Accent(_isDark);

        var title = new TextBlock
        {
            Text = TranslationHelper.Get("Update_Title", failsafe: "Software Update"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var close = CreateButton("\uE711", primary: false, subtle: true);
        close.FontFamily = new FontFamily("Segoe MDL2 Assets");
        close.FontSize = 12;
        close.Width = 30;
        close.Padding = new Thickness(0);
        close.Click += (_, _) => Close();

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        var icon = new TextBlock
        {
            Text = "\uE896",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        HeaderRow(header, icon, title);

        var headline = new TextBlock
        {
            Text = string.Format(
                TranslationHelper.Get("Update_FoundInline", failsafe: "Version {0} is available"), release.Version),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        var currentText = string.Format(
            TranslationHelper.Get("Update_CurrentVersion", failsafe: "Current version {0}"),
            currentVersion);
        if (release.PublishedAt is { } published)
        {
            currentText += "  ·  " + string.Format(
                TranslationHelper.Get("Update_Released", failsafe: "Released {0}"),
                published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture));
        }

        var current = new TextBlock
        {
            Text = currentText,
            Foreground = ThemePalette.SecondaryText(_isDark),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 2),
        };

        var body = new TextBlock
        {
            Text = TranslationHelper.Get("Update_Review",
                failsafe: "Review the changes below, then update now or skip this version."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemePalette.SecondaryText(_isDark),
            LineHeight = 19,
            Margin = new Thickness(0, 8, 0, 10),
        };

        var update = CreateButton(TranslationHelper.Get("Update_Now", failsafe: "Update now"), primary: true, subtle: false);
        update.IsDefault = true;
        update.Click += (_, _) =>
        {
            _updateNow = true;
            Close();
        };

        var ignore = CreateButton(TranslationHelper.Get("Update_Ignore", failsafe: "Skip this version"), primary: false, subtle: false);
        ignore.IsCancel = true;
        ignore.Margin = new Thickness(8, 0, 0, 0);
        ignore.Click += (_, _) => Close();

        _buttonLabels = $"update={update.Content};ignore={ignore.Content}";

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        actions.Children.Add(ignore);
        actions.Children.Add(update);

        var notesTitle = new TextBlock
        {
            Text = TranslationHelper.Get("Update_ReleaseNotes", failsafe: "Release notes"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var notesViewer = new FlowDocumentScrollViewer
        {
            Document = ReleaseNotesFormatter.BuildDocument(release.Notes, _isDark, OpenUrl),
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 10, 10, 10),
            IsSelectionEnabled = true,
        };

        var notesCard = new Border
        {
            Background = ThemePalette.ButtonBg(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = notesViewer,
            Margin = new Thickness(0, 7, 0, 14),
        };

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());

        if (!string.IsNullOrEmpty(release.ReleaseNotesUrl))
        {
            var github = CreateLink(
                TranslationHelper.Get("Update_ViewOnGitHub", failsafe: "View on GitHub"));
            github.Click += (_, _) => OpenUrl(release.ReleaseNotesUrl);
            Grid.SetColumn(github, 0);
            footer.Children.Add(github);
        }

        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);

        var info = new StackPanel();
        info.Children.Add(headline);
        info.Children.Add(current);
        info.Children.Add(body);

        var notesHeader = new Grid();
        notesHeader.ColumnDefinitions.Add(new ColumnDefinition());
        notesHeader.Children.Add(notesTitle);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(info, 1);
        Grid.SetRow(notesHeader, 2);
        Grid.SetRow(notesCard, 3);
        Grid.SetRow(footer, 4);
        content.Children.Add(header);
        content.Children.Add(info);
        content.Children.Add(notesHeader);
        content.Children.Add(notesCard);
        content.Children.Add(footer);

        var panel = new Border
        {
            Background = MenuSurface.SurfaceBrush(_isDark, MenuSurface.SurfaceProminence.Panel),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16, 20, 18),
            Child = content,
        };

        // v5.3.0: Tab cycles through the prompt's own answers, not the whole app.
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        return panel;
    }

    private static void HeaderRow(DockPanel header, UIElement icon, UIElement title)
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(icon);
        titleRow.Children.Add(title);
        DockPanel.SetDock(titleRow, Dock.Left);
        header.Children.Add(titleRow);
    }

    private void ApplyBackdrop()
    {
        // Same pipeline as the tray menu: no DWM backdrop, WCA acrylic instead, so
        // the blur follows the rounded panel and the window has no native frame.
        WindowHelper.DisableDwmBlur(this);
        _accentApplied = Helpers.MenuSurface.Apply(this, _isDark,
            Helpers.MenuSurface.SurfaceProminence.Panel);
    }

    private Color GetTintColor()
        => _isDark ? Color.FromRgb(0x2A, 0x24, 0x20) : Color.FromRgb(0xF8, 0xF6, 0xF4);

    private Button CreateButton(string label, bool primary, bool subtle)
    {
        var background = primary
            ? ThemePalette.Accent(_isDark)
            : subtle
                ? Brushes.Transparent
                : ThemePalette.ButtonBg(_isDark);

        var hover = primary
            ? PanelStyles.WithOpacity(ThemePalette.Accent(_isDark), 0.85)
            : ThemePalette.ButtonHover(_isDark);

        var button = new Button
        {
            Content = label,
            Background = background,
            Foreground = primary ? ContrastText(background) : ThemePalette.Text(_isDark),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            MinWidth = subtle ? 0 : 88,
            Padding = subtle ? new Thickness(6, 4, 6, 4) : new Thickness(16, 7, 16, 7),
            Template = PanelStyles.ButtonTemplate(hover),
        };

        // v5.3.0: a name for screen readers (the buttons use a custom template).
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        return button;
    }

    private Button CreateLink(string label)
    {
        var button = CreateButton(label, primary: false, subtle: true);
        button.Foreground = ThemePalette.Accent(_isDark);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    /// <summary>White on a dark accent, near-black on a light one.</summary>
    private static Brush ContrastText(Brush background)
    {
        if (background is not SolidColorBrush solid)
            return Brushes.White;

        var c = solid.Color;
        var luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255d;
        return luminance > 0.6
            ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))
            : Brushes.White;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // No default browser; ignore.
        }
    }
}
