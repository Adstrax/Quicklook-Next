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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLookNext;

/// <summary>
/// v5.0.8: shows the text the OCR engine found in the previewed image (upstream #1608). The text
/// is selectable and can be copied in one click; the panel reports why it is empty when nothing
/// was recognized and points at the Windows language settings when OCR is not installed at all.
/// </summary>
internal sealed class OcrWindow : Window
{
    private readonly string _path;
    private readonly bool _isDark;
    private readonly bool _smokeMode;

    private readonly TextBlock _status;
    private readonly TextBox _text;
    private readonly Button _copy;

    private string _result = string.Empty;
    private bool _accentApplied;

    private OcrWindow(string path, bool smokeMode)
    {
        _path = path;
        _smokeMode = smokeMode;
        _isDark = TrayIconManager.IsDarkTheme();

        Title = TranslationHelper.Get("OCR_Title", failsafe: "Extracted text");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ShowInTaskbar = !smokeMode;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));
        FontSize = 13;
        Foreground = ThemePalette.Text(_isDark);
        Background = Brushes.Transparent;

        _status = new TextBlock
        {
            Text = TranslationHelper.Get("OCR_Recognizing", failsafe: "Recognizing..."),
            Foreground = ThemePalette.SecondaryText(_isDark),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        _text = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = ThemePalette.ButtonBg(_isDark),
            Foreground = ThemePalette.Text(_isDark),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            MinHeight = 200,
            MaxHeight = 320,
            FontSize = 13,
        };

        _copy = CreateButton(TranslationHelper.Get("OCR_Copy", failsafe: "Copy"), primary: true, subtle: false);
        _copy.IsEnabled = false;
        _copy.Click += (_, _) => CopyText();

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

        Loaded += (_, _) => Recognize();
    }

    internal static void ShowWindow(string path, bool smokeMode = false)
    {
        var window = new OcrWindow(path, smokeMode);
        window.Show();
        window.Activate();
    }

    private FrameworkElement BuildContent()
    {
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 12) };

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
            Text = "\uE8FE", // Segoe MDL2 Assets: Scan
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = ThemePalette.Accent(_isDark),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var title = new TextBlock
        {
            Text = TranslationHelper.Get("OCR_Title", failsafe: "Extracted text"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(icon);
        titleRow.Children.Add(title);
        DockPanel.SetDock(titleRow, Dock.Left);
        header.Children.Add(titleRow);

        var cancel = CreateButton(TranslationHelper.Get("OCR_Close", failsafe: "Close"), primary: false, subtle: false);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => Close();

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        actions.Children.Add(cancel);
        actions.Children.Add(_copy);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(_status);
        content.Children.Add(_text);
        content.Children.Add(actions);

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

    private async void Recognize()
    {
        try
        {
            _result = await Task.Run(() => OcrRecognizer.RecognizeAsync(_path).GetAwaiter().GetResult());
        }
        catch (InvalidOperationException)
        {
            _status.Text = TranslationHelper.Get("OCR_Unavailable",
                failsafe: "OCR is unavailable - install a language pack in Windows Settings " +
                          "(Time & language → Language & region).");
            WriteSmokeDiagnostics("unavailable");
            return;
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Text recognition failed for \"{_path}\": {e}");
            _status.Text = TranslationHelper.Get("OCR_Failed",
                failsafe: "Text recognition failed - see the log for details.");
            WriteSmokeDiagnostics("failed");
            return;
        }

        _text.Text = _result;
        _copy.IsEnabled = _result.Length > 0;
        _status.Text = _result.Length > 0
            ? string.Format(
                TranslationHelper.Get("OCR_Recognized", failsafe: "Recognized {0} characters."),
                _result.Length)
            : TranslationHelper.Get("OCR_Empty", failsafe: "No text was recognized.");

        WriteSmokeDiagnostics(_result.Length > 0 ? "ok" : "empty");
    }

    private void CopyText()
    {
        if (_result.Length == 0)
            return;

        try
        {
            Clipboard.SetText(_result);
            _status.Text = TranslationHelper.Get("OCR_Copied", failsafe: "Text copied to the clipboard.");
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Copying the recognized text failed: {e}");
            _status.Text = TranslationHelper.Get("OCR_Failed",
                failsafe: "Text recognition failed - see the log for details.");
        }
    }

    /// <summary>
    /// /test-ocr (with QL_TEST_OCR_FILE pointing at an image) drops the result into
    /// &lt;smokeDir&gt;\ocr.txt and closes, so the whole path can be checked without a click.
    /// </summary>
    private void WriteSmokeDiagnostics(string status)
    {
        if (!_smokeMode || string.IsNullOrEmpty(App.SmokeDir))
            return;

        try
        {
            Directory.CreateDirectory(App.SmokeDir);

            // v5.0.11: which engine won, and what every other engine answered - the language
            // choice is what decides the quality, so the run has to be visible.
            var attempts = string.Join("; ", OcrRecognizer.LastAttempts.Select(a =>
                $"{a.Language}:score={a.Score},script={a.InScript}/{a.Characters}"));

            File.WriteAllText(
                Path.Combine(App.SmokeDir, "ocr.txt"),
                $"path={_path}\nstatus={status}\nchars={_result.Length}\n" +
                $"attempts={attempts}\n" +
                $"text={_result.Replace("\r\n", "\n").Replace('\r', '\n')}\n");
        }
        catch
        {
            // Diagnostics must never affect the panel.
        }

        // Stay on screen for a moment (like the other smoke hooks) so a screenshot can be taken.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        timer.Start();
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

    /// <summary>Same flat, rounded button as the data/cache panel - see its note on consolidation.</summary>
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
