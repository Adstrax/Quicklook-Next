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

        RefreshList();
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

        pluginList.Items.Clear();
        foreach (var entry in _entries)
            pluginList.Items.Add(BuildRow(entry));

        var userCount = _entries.Count(e => e.IsUserPlugin);
        var builtInCount = _entries.Count - userCount;
        headerText.Text = string.Format(
            Tr("PM_Header", "Installed Plugins ({0} user, {1} built-in)"),
            userCount, builtInCount);

        statusText.Text = userCount == 0
            ? Tr("PM_None", "No user-installed plugins yet. Preview a .qlplugin file to install one.")
            : string.Empty;
    }

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
                Text = "\uEA86", // Segoe MDL2 Assets: Puzzle
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

        var version = new TextBlock
        {
            Text = entry.Version,
            Foreground = (Brush)Resources["SecondaryTextBrush"],
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
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

        var badge = new Border
        {
            Background = (Brush)Resources["BadgeBrush"],
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = entry.IsUserPlugin
                    ? Tr("PM_UserPlugin", "User")
                    : Tr("PM_BuiltIn", "Built-in"),
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
        SetBrush("TintBrush", ThemePalette.Tint(_isDark));
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
        Helpers.MenuSurface.Apply(this, _isDark);
        _accentApplied = true;
    }

    private Color GetTintColor()
    {
        return _isDark ? Color.FromRgb(0x2A, 0x24, 0x20) : Color.FromRgb(0xF8, 0xF6, 0xF4);
    }
}
