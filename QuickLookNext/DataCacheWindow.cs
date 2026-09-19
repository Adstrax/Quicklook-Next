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

using QuickLook.Common.ExtensionMethods;
using QuickLook.Common.Helpers;
using QuickLookNext.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLookNext;

/// <summary>
/// v5.0.7: the "data &amp; cache" panel behind the tray entry of the same name - upstream issue
/// #1933 asked for a way to see and clear what the app stores. It reports how much of the data
/// folder is rebuildable cache (WebView2 shader/HTTP caches, leftover update packages) versus
/// kept data (settings, cookies, statistics, log), and clears only the former - see
/// <see cref="AppDataUsage"/> for the whitelist.
/// </summary>
internal sealed class DataCacheWindow : Window
{
    private static DataCacheWindow _instance;

    private readonly bool _isDark;
    private readonly bool _smokeMode;
    private readonly TextBlock _cacheValue;
    private readonly TextBlock _dataValue;
    private readonly TextBlock _totalValue;
    private readonly TextBlock _status;
    private readonly string _buttonLabels;

    private bool _busy;
    private bool _accentApplied;

    private DataCacheWindow(bool smokeMode)
    {
        _isDark = TrayIconManager.IsDarkTheme();
        _smokeMode = smokeMode;

        Title = TranslationHelper.Get("DataCache_Title", failsafe: "Data & cache");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ShowInTaskbar = !smokeMode;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));
        FontSize = 13;
        Foreground = ThemePalette.Text(_isDark);
        Background = Brushes.Transparent;

        (_cacheValue, _dataValue, _totalValue, _status, _buttonLabels) = BuildValues();
        Content = BuildContent();

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
                    // Clicking without moving throws.
                }
            }
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };

        Loaded += (_, _) => Refresh();
    }

    internal static void ShowWindow(bool smokeMode = false)
    {
        if (_instance is { IsLoaded: true } existing)
        {
            existing.Activate();
            return;
        }

        _instance = new DataCacheWindow(smokeMode);
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    /// <summary>Reported by /test-data-cache, so the numbers can be asserted later.</summary>
    internal string Diagnose()
        => $"title={Title}\n" +
           $"cache={AppDataUsage.CacheBytes()}\n" +
           $"data={AppDataUsage.DataBytes()}\n" +
           $"total={AppDataUsage.TotalBytes()}\n" +
           $"buttons={_buttonLabels}\n" +
           $"backdrop={MenuSurface.Diagnose()}\n";

    private (TextBlock, TextBlock, TextBlock, TextBlock, string) BuildValues()
    {
        var cache = ValueText();
        var data = ValueText();
        var total = ValueText();

        var status = new TextBlock
        {
            Text = string.Empty,
            Foreground = ThemePalette.SecondaryText(_isDark),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };

        return (cache, data, total, status, "clear=" + TranslationHelper.Get("DataCache_Clear", failsafe: "Clear cache"));
    }

    private TextBlock ValueText()
        => new()
        {
            Text = TranslationHelper.Get("DataCache_Measuring", failsafe: "Calculating..."),
            HorizontalAlignment = HorizontalAlignment.Right,
            FontWeight = FontWeights.SemiBold,
        };

    private FrameworkElement BuildContent()
    {
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };

        var close = CreateButton("\uE711", primary: false, subtle: true);
        close.FontFamily = new FontFamily("Segoe MDL2 Assets");
        close.FontSize = 12;
        close.Width = 30;
        close.Padding = new Thickness(0);
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        var icon = new TextBlock
        {
            Text = "\uEA99", // Segoe MDL2 Assets: Broom
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = ThemePalette.Accent(_isDark),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var title = new TextBlock
        {
            Text = TranslationHelper.Get("DataCache_Title", failsafe: "Data & cache"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(icon);
        titleRow.Children.Add(title);
        DockPanel.SetDock(titleRow, Dock.Left);
        header.Children.Add(titleRow);

        var rows = new StackPanel();
        rows.Children.Add(Row(TranslationHelper.Get("DataCache_Cache", failsafe: "Cache"), _cacheValue));
        rows.Children.Add(Row(TranslationHelper.Get("DataCache_Data", failsafe: "Settings & logs"), _dataValue));
        rows.Children.Add(Row(TranslationHelper.Get("DataCache_Total", failsafe: "Total"), _totalValue));

        var hint = new TextBlock
        {
            Text = TranslationHelper.Get("DataCache_Hint",
                failsafe: "Clearing removes only rebuildable caches (WebView2 shader and web caches, " +
                          "leftover update files). Sign-in data, settings and the log are kept."),
            Foreground = ThemePalette.SecondaryText(_isDark),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var openFolder = CreateButton(
            TranslationHelper.Get("DataCache_OpenFolder", failsafe: "Open data folder"), primary: false, subtle: true);
        openFolder.Click += (_, _) =>
        {
            try
            {
                Process.Start("explorer.exe", SettingHelper.LocalDataPath);
            }
            catch (Exception e)
            {
                ProcessHelper.WriteLog($"Opening the data folder failed: {e}");
            }
        };

        var cancel = CreateButton(
            TranslationHelper.Get("DataCache_Close", failsafe: "Close"), primary: false, subtle: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();

        var clear = CreateButton(
            TranslationHelper.Get("DataCache_Clear", failsafe: "Clear cache"), primary: true, subtle: false);
        clear.Click += async (_, _) => await ClearAsync();

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        cancel.Margin = new Thickness(0, 0, 8, 0);
        actions.Children.Add(cancel);
        actions.Children.Add(clear);

        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(openFolder, 0);
        footer.Children.Add(openFolder);
        Grid.SetColumn(actions, 1);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        footer.Children.Add(actions);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(rows);
        content.Children.Add(hint);
        content.Children.Add(_status);
        content.Children.Add(footer);

        return new Border
        {
            Background = ThemePalette.Tint(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16, 20, 18),
            Child = content,
        };
    }

    private static FrameworkElement Row(string label, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        return grid;
    }

    private async Task RefreshAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            var (cache, data) = await Task.Run(() => (AppDataUsage.CacheBytes(), AppDataUsage.DataBytes()));

            _cacheValue.Text = cache.ToPrettySize(1);
            _dataValue.Text = data.ToPrettySize(1);
            _totalValue.Text = (cache + data).ToPrettySize(1);
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Data/cache measurement failed: {e}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void Refresh()
    {
        if (_smokeMode)
        {
            // /test-data-cache: measure synchronously, dump the numbers, then go away.
            _cacheValue.Text = AppDataUsage.CacheBytes().ToPrettySize(1);
            _dataValue.Text = AppDataUsage.DataBytes().ToPrettySize(1);
            _totalValue.Text = AppDataUsage.TotalBytes().ToPrettySize(1);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    if (string.IsNullOrEmpty(App.SmokeDir))
                        return;

                    Directory.CreateDirectory(App.SmokeDir);
                    File.WriteAllText(Path.Combine(App.SmokeDir, "data-cache.txt"), Diagnose());
                }
                catch
                {
                    // Diagnostics must never affect the panel.
                }

                Close();
            };
            timer.Start();
            return;
        }

        _ = RefreshAsync();
    }

    private async Task ClearAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            var result = await Task.Run(() => AppDataUsage.ClearCache());

            var message = result.FreedBytes == 0 && result.Complete
                ? TranslationHelper.Get("DataCache_NothingToClear", failsafe: "Nothing to clear.")
                : result.Complete
                    ? string.Format(
                        TranslationHelper.Get("DataCache_Cleared", failsafe: "Freed {0}."),
                        result.FreedBytes.ToPrettySize(1))
                    : string.Format(
                        TranslationHelper.Get("DataCache_ClearPartial",
                            failsafe: "Freed {0}; some files were in use - try again later."),
                        result.FreedBytes.ToPrettySize(1));

            _status.Text = message;
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Cache cleanup failed: {e}");
            _status.Text = TranslationHelper.Get("DataCache_ClearFailed",
                failsafe: "Cleaning the cache failed - see the log for details.");
        }
        finally
        {
            _busy = false;
        }

        await RefreshAsync();
    }

    private Button CreateButton(string label, bool primary, bool subtle)
    {
        var background = primary
            ? ThemePalette.Accent(_isDark)
            : subtle
                ? Brushes.Transparent
                : ThemePalette.ButtonBg(_isDark);

        var hover = primary
            ? WithOpacity(ThemePalette.Accent(_isDark), 0.85)
            : ThemePalette.ButtonHover(_isDark);

        return new Button
        {
            Content = label,
            Background = background,
            Foreground = primary ? ContrastText(background) : ThemePalette.Text(_isDark),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            MinWidth = subtle ? 0 : 88,
            Padding = subtle ? new Thickness(6, 4, 6, 4) : new Thickness(16, 7, 16, 7),
            Template = BuildButtonTemplate(hover),
        };
    }

    /// <summary>
    /// Rounded, flat button - the stock WPF template paints a grey gradient that does not belong
    /// on the acrylic panel (same recipe as the update dialogs; they still carry their own copy
    /// of this helper, worth consolidating the next time they are touched).
    /// </summary>
    private static ControlTemplate BuildButtonTemplate(Brush hover)
    {
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        template.Triggers.Add(hoverTrigger);
        return template;
    }

    private static Brush WithOpacity(Brush brush, double opacity)
    {
        if (brush is not SolidColorBrush solid)
            return brush;

        var copy = new SolidColorBrush(solid.Color) { Opacity = opacity };
        copy.Freeze();
        return copy;
    }

    private static Brush ContrastText(Brush background)
        => background is SolidColorBrush { Color: var color } &&
           0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 160
            ? Brushes.Black
            : Brushes.White;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (!_accentApplied)
            ApplyBackdrop();
    }

    private void ApplyBackdrop()
    {
        WindowHelper.DisableDwmBlur(this);
        _accentApplied = MenuSurface.Apply(this, _isDark);
    }
}
