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
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLookNext;

/// <summary>
/// v3.43.0: what the user sees while an update is downloaded.
/// <para>
/// Before this, clicking "update now" produced one toast and then nothing visible
/// for the length of a ~62 MB download - on a slow connection the app looked
/// stuck until it suddenly exited to install. The download now reports its
/// progress here, in the same acrylic panel as the update prompt, and the panel
/// stays until the app restarts into the new version.
/// </para>
/// </summary>
internal sealed class UpdateProgressDialog : Window
{
    private readonly bool _isDark;
    private readonly Action _onCancel;
    private bool _accentApplied;
    private bool _cancelled;

    private readonly Border _track;
    private readonly Border _fill;
    private readonly TextBlock _headline;
    private readonly TextBlock _detail;
    private readonly TextBlock _status;

    private UpdateProgressDialog(string version, Action onCancel)
    {
        _isDark = TrayIconManager.IsDarkTheme();
        _onCancel = onCancel;

        Title = TranslationHelper.Get("Update_Title", failsafe: "软件更新");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));
        FontSize = 13;
        Foreground = ThemePalette.Text(_isDark);
        // v5.3.0: same firmer panel surface as the update prompt.
        Background = MenuSurface.SurfaceBrush(_isDark, MenuSurface.SurfaceProminence.Panel);

        _headline = new TextBlock
        {
            Text = string.Format(
                TranslationHelper.Get("Update_Downloading", failsafe: "正在下载 {0}"), version),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        _detail = new TextBlock
        {
            Foreground = ThemePalette.SecondaryText(_isDark),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 10),
        };

        _fill = new Border
        {
            Background = ThemePalette.Accent(_isDark),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0,
        };

        _track = new Border
        {
            Background = ThemePalette.ButtonBg(_isDark),
            CornerRadius = new CornerRadius(3),
            Height = 6,
            ClipToBounds = true,
            Child = _fill,
        };

        _status = new TextBlock
        {
            Text = TranslationHelper.Get("Update_DownloadHint",
                failsafe: "下载完成后会自动安装并重启，当前预览不会丢失。"),
            Foreground = ThemePalette.SecondaryText(_isDark),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };

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
                    // Released before the drag started.
                }
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Cancel();
        };
    }

    /// <summary>Must be called on the UI thread.</summary>
    internal static UpdateProgressDialog Show(string version, Action onCancel = null)
        => new(version, onCancel);

    /// <summary>Whether the user asked to stop the download.</summary>
    internal bool IsCancelled => _cancelled;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ApplyBackdrop();
    }

    /// <summary>
    /// v3.43.0: download progress. <paramref name="total"/> is null when the server
    /// does not announce a length; the bar then shows the received amount only.
    /// </summary>
    internal void Report(long received, long? total)
    {
        var width = _track.ActualWidth > 0 ? _track.ActualWidth : Math.Max(0, Width - 40);

        if (total is > 0)
        {
            var fraction = Math.Clamp((double)received / total.Value, 0, 1);
            _fill.Width = width * fraction;
            _detail.Text = $"{fraction * 100:0}%  ·  {Size(received)} / {Size(total.Value)}";
        }
        else
        {
            _fill.Width = width;
            _detail.Text = Size(received);
        }
    }

    /// <summary>Marks the download as indeterminate (no announced length).</summary>
    internal void MarkUnknownLength()
    {
        _fill.Width = _track.ActualWidth > 0 ? _track.ActualWidth : Math.Max(0, Width - 40);
        _fill.Opacity = 0.5;
        _detail.Text = TranslationHelper.Get("Update_DownloadingUnknown", failsafe: "正在下载…");
    }

    internal void SetStatus(string text)
    {
        _status.Text = text;
    }

    internal void SetHeadline(string text)
    {
        _headline.Text = text;
    }

    internal void CloseSafely()
    {
        try
        {
            Close();
        }
        catch
        {
            // The window may already be gone (app shutting down).
        }
    }

    /// <summary>
    /// v3.43.0 test hook: when the smoke test asked for the download panel, feed it a
    /// fake download and record what it looked like - so the smoke test can assert the
    /// material and the progress without downloading 62 MB.
    /// <para>
    /// v5.0.11: gated on the /test-update-progress switch instead of "a smoke directory
    /// exists" - the latter is always true (it falls back to %TEMP%\ql-smoke), which
    /// turned the hook into a trap for any future caller on the real download path.
    /// </para>
    /// </summary>
    internal void RunSmokeTestHook()
    {
        if (!App.IsUpdateProgressTestEnabled)
            return;

        var smokeDir = App.SmokeDir;

        var received = 0L;
        const long total = 64L * 1024 * 1024;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        timer.Tick += (_, _) =>
        {
            received += total / 6;
            if (received > total)
                received = total;

            Report(received, total);

            if (received < total)
                return;

            timer.Stop();
            SetStatus(TranslationHelper.Get("Update_Installing",
                failsafe: "下载完成，正在安装并重启…"));

            try
            {
                Directory.CreateDirectory(smokeDir);
                File.WriteAllText(Path.Combine(smokeDir, "update-progress.txt"),
                    $"title={Title}\nbackdrop={DiagnoseBackdrop()}\n" +
                    $"size={ActualWidth:0}x{ActualHeight:0}\ndetail={_detail.Text}\n" +
                    $"status={_status.Text}\nlanguage={CultureInfo.CurrentUICulture.Name}\n");
            }
            catch
            {
                // Diagnostics must never affect the download.
            }

            CloseSafely();
        };

        timer.Start();
    }

    internal string DiagnoseBackdrop() => $"accent-applied={_accentApplied} {Helpers.MenuSurface.Diagnose()}";

    private FrameworkElement BuildContent()
    {
        var icon = new TextBlock
        {
            Text = "\uE896",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = ThemePalette.Accent(_isDark),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var title = new TextBlock
        {
            Text = TranslationHelper.Get("Update_Title", failsafe: "软件更新"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(icon);
        header.Children.Add(title);

        var cancel = new Button
        {
            Content = TranslationHelper.Get("Update_Cancel", failsafe: "取消"),
            Background = ThemePalette.ButtonBg(_isDark),
            Foreground = ThemePalette.Text(_isDark),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            MinWidth = 88,
            Padding = new Thickness(16, 7, 16, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Template = ButtonTemplate(ThemePalette.ButtonHover(_isDark)),
        };
        cancel.Click += (_, _) => Cancel();

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(_headline);
        content.Children.Add(_detail);
        content.Children.Add(_track);
        content.Children.Add(_status);
        content.Children.Add(cancel);

        return new Border
        {
            Background = MenuSurface.SurfaceBrush(_isDark, MenuSurface.SurfaceProminence.Panel),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16, 20, 18),
            Child = content,
        };
    }

    private static ControlTemplate ButtonTemplate(Brush hover)
    {
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hover) { TargetName = "bd" });
        template.Triggers.Add(hoverTrigger);

        return template;
    }

    private void Cancel()
    {
        if (_cancelled)
            return;

        _cancelled = true;
        _onCancel?.Invoke();
        CloseSafely();
    }

    private void ApplyBackdrop()
    {
        WindowHelper.DisableDwmBlur(this);
        _accentApplied = Helpers.MenuSurface.Apply(this, _isDark,
            Helpers.MenuSurface.SurfaceProminence.Panel);
    }

    private Color GetTintColor()
        => _isDark ? Color.FromRgb(0x2A, 0x24, 0x20) : Color.FromRgb(0xF8, 0xF6, 0xF4);

    /// <summary>
    /// v5.0.1: "12.3 MB", never the full double the progress arithmetic produces
    /// (it used to read like "60.12675467123377 MB"). Small values stay in KB so the
    /// first moments of a download still show movement.
    /// </summary>
    private static string Size(long bytes)
        => bytes < 1024L * 1024L
            ? $"{bytes / 1024d:0} KB"
            : $"{bytes / 1024d / 1024d:0.0} MB";
}
