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
using QuickLook.Common.Controls;
using QuickLookNext.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickLookNext;

/// <summary>
/// v1.3.11: plugin management panel. Lists both built-in and user-installed
/// plugins, and lets the user uninstall plugins that live in the user plugin
/// folder (built-in plugins ship with the app and are marked as such).
/// </summary>
public partial class PluginManagerWindow : Window
{
    private readonly bool _isDark;
    private readonly List<PluginEntry> _entries = [];
    private bool _accentApplied;

    public PluginManagerWindow()
    {
        InitializeComponent();

        _isDark = TrayIconManager.IsDarkTheme();
        ApplyTheme();

        Title = Tr("PM_Title", "Manage Plugins");
        btnOpenFolder.Content = Tr("PM_OpenFolder", "Open Plugin Folder");
        btnRefresh.Content = Tr("PM_Refresh", "Refresh");
        btnClose.Content = Tr("PM_Close", "Close");

        // v5.3.0: the panel opens with the cursor in the search box - with 25 plugins the filter
        // is the first thing most people reach for.
        var searchLabel = Tr("PM_Search", "Search plugins");
        searchPlaceholder.Text = searchLabel;
        System.Windows.Automation.AutomationProperties.SetName(searchBox, searchLabel);

        RefreshList();

        Loaded += (_, _) => searchBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Re-apply after first paint, like the tray menu and the preview
        // window do; some windows need the attribute set once visible.
        ApplyBackdrop();
    }

    /// <summary>
    /// Opens the manager, reusing the existing window when one is already open.
    /// </summary>
    internal static void ShowWindow()
    {
        var existing = Application.Current.Windows
            .OfType<PluginManagerWindow>()
            .FirstOrDefault(w => w.IsVisible);
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        new PluginManagerWindow().Show();
    }

    /// <summary>
    /// Test hook: dump the currently listed plugins (name + user marker) so
    /// the smoke test can assert the panel enumerates installed plugins.
    /// </summary>
    internal string DiagnosePlugins()
    {
        return string.Join("|", _entries.Select(e => e.Name + (e.IsUserPlugin ? "*" : string.Empty)));
    }

    /// <summary>
    /// Test hook: reports whether the WCA acrylic call succeeded, matching the
    /// tray menu's diagnostics so the smoke test can assert the same backdrop.
    /// </summary>
    internal string DiagnoseBackdrop() => $"accent-applied={_accentApplied} {Helpers.MenuSurface.Diagnose()}";

    private void RefreshList()
    {
        _entries.Clear();
        _entries.AddRange(PluginManager.GetInstance().EnumerateInstalledPlugins());

        // v5.3.0: 25 plugins are past the point where scrolling is a search interface.
        var filter = searchBox?.Text?.Trim() ?? string.Empty;
        var visible = string.IsNullOrEmpty(filter)
            ? _entries
            : _entries.Where(e =>
                e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (e.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

        pluginList.Items.Clear();
        foreach (var entry in visible)
            pluginList.Items.Add(BuildRow(entry));

        var userCount = _entries.Count(e => e.IsUserPlugin);
        var builtInCount = _entries.Count - userCount;
        headerText.Text = string.Format(
            Tr("PM_Header", "Installed Plugins ({0} user, {1} built-in)"),
            userCount, builtInCount);

        if (!string.IsNullOrEmpty(filter))
        {
            statusText.Text = visible.Count == 0
                ? string.Format(Tr("PM_NoMatch", "No plugin matches \u201c{0}\u201d."), filter)
                : string.Format(Tr("PM_Matches", "{0} of {1} shown."), visible.Count, _entries.Count);
            return;
        }

        statusText.Text = userCount == 0
            ? Tr("PM_None", "No user-installed plugins yet. Preview a .qlplugin file to install one.")
            : string.Empty;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (searchPlaceholder != null)
            searchPlaceholder.Visibility = string.IsNullOrEmpty(searchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

        RefreshList();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        // First Esc clears the filter, the next one closes the panel - the same rule the dialogs
        // use for their own Esc handling.
        if (!string.IsNullOrEmpty(searchBox.Text))
        {
            searchBox.Clear();
            e.Handled = true;
            return;
        }

        Close();
        e.Handled = true;
    }

    /// <summary>
    /// v5.3.0: the MDL2 glyph that stands for what the plugin previews. One glyph per family -
    /// the rows used to be 25 identical puzzle pieces, which made the list impossible to scan.
    /// </summary>
    private static string GlyphFor(string pluginName)
    {
        var key = pluginName?.Replace("Viewer", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim() ?? string.Empty;

        return key switch
        {
            "Image" or "Thumbnail" => FontSymbols.Photo,
            "Video" => FontSymbols.Video,
            "Text" => FontSymbols.Document,
            "Markdown" => FontSymbols.PageSolid,
            "Html" or "Chm" => FontSymbols.Globe,
            "Mail" => FontSymbols.Mail,
            "Font" => FontSymbols.Font,
            "Csv" or "Db" => FontSymbols.Library,
            "Archive" => FontSymbols.ZipFolder,
            "Office" => FontSymbols.Slideshow,
            "MediaInfo" => FontSymbols.Info,
            "Cert" => FontSymbols.Certificate,
            "App" => FontSymbols.AllApps,
            "Binary" or "PE" or "ELF" or "Dump" or "Prefetch" => FontSymbols.Code,
            "CLSID" => FontSymbols.Tag,
            "Helix" => FontSymbols.Media,
            _ => pluginName?.Contains("Plugin", StringComparison.OrdinalIgnoreCase) == true
                || pluginName?.Contains("Installer", StringComparison.OrdinalIgnoreCase) == true
                    ? FontSymbols.Download
                    : FontSymbols.Puzzle,
        };
    }

    /// <summary>
    /// v5.3.0: a plugin built without an AssemblyVersion reports 0.0.0.0; the panel hides that
    /// instead of printing something that reads like a bug.
    /// </summary>
    private static string ReadableVersion(string version)
        => string.IsNullOrWhiteSpace(version) || version.StartsWith("0.0.0", StringComparison.Ordinal)
            ? string.Empty
            : version;

    private Border BuildRow(PluginEntry entry)
    {
        // v3.7.0: a tinted plugin glyph makes each row read as a card instead
        // of a bare text line.
        var icon = new Border
        {
            Background = (Brush)Resources["ButtonBgBrush"],
            CornerRadius = new CornerRadius(6),
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                // v5.3.0: every row used the same puzzle glyph, which made 25 rows of a list
                // unreadable at a glance. Each family now shows what it previews.
                Text = GlyphFor(entry.Name),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = (Brush)Resources["SecondaryTextBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var name = new TextBlock
        {
            Text = entry.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Resources["TextBrush"],
            VerticalAlignment = VerticalAlignment.Center,
        };

        var versionText = ReadableVersion(entry.Version);

        var version = new TextBlock
        {
            // v5.3.0: "0.0.0.0" is what a plugin without an AssemblyVersion reports - showing
            // it looks like a defect in the panel, so an unknown version shows nothing.
            Text = versionText,
            Foreground = (Brush)Resources["SecondaryTextBrush"],
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrEmpty(versionText) ? Visibility.Collapsed : Visibility.Visible,
        };

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
        namePanel.Children.Add(name);
        namePanel.Children.Add(version);

        var description = new TextBlock
        {
            Text = entry.Description,
            Foreground = (Brush)Resources["SecondaryTextBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 16, 0),
            MaxWidth = 280,
        };

        // v5.3.0: the header already counts the built-in plugins ("0 user, 25 built-in"), so
        // repeating "Built-in" on every row was noise. Only the exceptions are marked.
        var badge = new Border
        {
            Background = (Brush)Resources["BadgeBrush"],
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = entry.IsUserPlugin ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = Tr("PM_UserPlugin", "User"),
                Foreground = (Brush)Resources["BadgeTextBrush"],
                FontSize = 11,
            },
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);
        Grid.SetColumn(namePanel, 1);
        grid.Children.Add(namePanel);
        Grid.SetColumn(description, 2);
        grid.Children.Add(description);
        Grid.SetColumn(badge, 3);
        grid.Children.Add(badge);

        if (entry.IsUserPlugin)
        {
            var uninstall = new Button
            {
                Content = Tr("PM_Uninstall", "Uninstall"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(10, 0, 0, 0),
                Foreground = (Brush)Resources["DangerBrush"],
                Style = (Style)Resources["PanelButtonStyle"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            // v5.3.0: a screen reader should say which plugin the button removes.
            System.Windows.Automation.AutomationProperties.SetName(
                uninstall, $"{Tr("PM_Uninstall", "Uninstall")} {entry.Name}");
            uninstall.Click += (_, _) => Uninstall(entry, uninstall);

            Grid.SetColumn(uninstall, 4);
            grid.Children.Add(uninstall);
        }

        var row = new Border
        {
            Child = grid,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = entry.Folder,
        };
        row.MouseEnter += (_, _) => row.Background = (Brush)Resources["RowHoverBrush"];
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        return row;
    }

    private void Uninstall(PluginEntry entry, Button button)
    {
        var message = string.Format(
            Tr("PM_ConfirmUninstall", "Uninstall plugin \"{0}\"? Files will be removed from your user plugins folder."),
            entry.Name);
        if (MessageBox.Show(this, message, Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        button.IsEnabled = false;
        if (PluginManager.GetInstance().UninstallUserPlugin(entry, out var error, out var restartRequired))
        {
            statusText.Text = restartRequired
                ? Tr("PM_RestartRequired", "Files are locked; the plugin will be fully removed after a restart.")
                : Tr("PM_Uninstalled", "Uninstalled. Restart to fully release the loaded files.");
            RefreshList();
        }
        else
        {
            button.IsEnabled = true;
            statusText.Text = error;
        }
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(App.UserPluginPath);
            Process.Start("explorer.exe", App.UserPluginPath);
        }
        catch (Exception ex)
        {
            statusText.Text = ex.Message;
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshList();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BtnHeaderClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ApplyTheme()
    {
        // v3.11.0: shared palette for both light and dark; the accent follows
        // the system accent (badge text / background come from the same
        // accent, so the panel matches the tray menu).
        // v5.3.0: the panel surface (firmer than the tray menu, and solid when the system has
        // transparency effects switched off) - see MenuSurface.
        SetBrush("TintBrush", Helpers.MenuSurface.SurfaceBrush(
            _isDark, Helpers.MenuSurface.SurfaceProminence.Panel));
        SetBrush("ContentPlateBrush", ThemePalette.ContentPlate(_isDark));
        SetBrush("FocusRingBrush", PanelStyles.FocusBrush());
        SetBrush("PanelBorderBrush", ThemePalette.Border(_isDark));
        SetBrush("TextBrush", ThemePalette.Text(_isDark));
        SetBrush("SecondaryTextBrush", ThemePalette.SecondaryText(_isDark));
        SetBrush("RowHoverBrush", ThemePalette.Hover(_isDark));
        SetBrush("SeparatorBrush", ThemePalette.Separator(_isDark));
        SetBrush("BadgeBrush", ThemePalette.AccentTint(_isDark));
        SetBrush("BadgeTextBrush", ThemePalette.Accent(_isDark));
        SetBrush("ButtonBgBrush", ThemePalette.ButtonBg(_isDark));
        SetBrush("ButtonHoverBrush", ThemePalette.ButtonHover(_isDark));
        SetBrush("DangerBrush", ThemePalette.Danger(_isDark));
        SetBrush("ScrollBarThumbBrush", ThemePalette.ScrollbarThumb(_isDark));

        // Dragging a borderless window: the header bar acts as the caption.
        headerBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch
                {
                    // Ignore drags that start during window activation.
                }
            }
        };
    }

    private void SetBrush(string key, Brush brush)
    {
        // Brushes declared in XAML resources are frozen and cannot be
        // mutated, so swap in a new unfrozen brush.
        Resources[key] = brush;
    }

    private static string Tr(string key, string failsafe)
    {
        return TranslationHelper.Get(key, failsafe: failsafe);
    }

    /// <summary>
    /// v1.3.12: same non-layered recipe as the tray menu - clear any DWM
    /// backdrop and restore rounded corners, then enable the WCA acrylic.
    /// </summary>
    private void ApplyBackdrop()
    {
        WindowHelper.DisableDwmBlur(this);
        _accentApplied = Helpers.MenuSurface.Apply(this, _isDark,
            Helpers.MenuSurface.SurfaceProminence.Panel);
    }

    private Color GetTintColor()
    {
        return _isDark ? Color.FromRgb(0x2A, 0x24, 0x20) : Color.FromRgb(0xF8, 0xF6, 0xF4);
    }
}
