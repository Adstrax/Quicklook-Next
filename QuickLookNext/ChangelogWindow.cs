// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Common.Controls;
using QuickLook.Common.Helpers;
using QuickLookNext.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace QuickLookNext;

/// <summary>
/// On-demand release history. The release feed is fetched only while this
/// window is open; the app does not keep a changelog cache or a background
/// client alive after the window closes.
/// </summary>
internal sealed class ChangelogWindow : Window
{
    private static ChangelogWindow _instance;

    private readonly bool _isDark;
    private readonly string _preferredVersion;
    private readonly StackPanel _releaseList = new();
    private readonly FlowDocumentScrollViewer _notesViewer;
    private readonly TextBlock _versionText;
    private readonly TextBlock _dateText;
    private readonly TextBlock _statusText;
    private readonly Button _githubButton;
    private readonly Button _refreshButton;
    private readonly Button _retryButton;

    private CancellationTokenSource _loadCancellation;
    private IReadOnlyList<UpdateReleaseInfo> _releases = Array.Empty<UpdateReleaseInfo>();
    private Button _selectedItem;
    private UpdateReleaseInfo _selectedRelease;
    private bool _closed;

    private ChangelogWindow(string preferredVersion)
    {
        _preferredVersion = preferredVersion;
        _isDark = TrayIconManager.IsDarkTheme();

        Title = TranslationHelper.Get("Update_HistoryTitle", failsafe: "Update History");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        ResizeMode = ResizeMode.CanResize;
        Width = Math.Max(560, Math.Min(800, SystemParameters.WorkArea.Width - 64));
        Height = Math.Max(440, Math.Min(680, SystemParameters.WorkArea.Height - 64));
        MinWidth = 540;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));
        FontSize = 13;
        Foreground = ThemePalette.Text(_isDark);
        Background = MenuSurface.SurfaceBrush(_isDark, MenuSurface.SurfaceProminence.Panel);

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        _notesViewer = new FlowDocumentScrollViewer
        {
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16, 12, 12, 12),
            IsSelectionEnabled = true,
            Document = EmptyDocument(),
        };
        _versionText = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        _dateText = new TextBlock
        {
            Foreground = ThemePalette.SecondaryText(_isDark),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 10),
        };
        _statusText = new TextBlock
        {
            Foreground = ThemePalette.SecondaryText(_isDark),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 10),
        };

        _githubButton = CreateButton(
            TranslationHelper.Get("Update_ViewOnGitHub", failsafe: "View on GitHub"),
            primary: false,
            subtle: true);
        _githubButton.Foreground = ThemePalette.Accent(_isDark);
        _githubButton.IsEnabled = false;
        _githubButton.Click += (_, _) => OpenUrl(_selectedRelease?.ReleaseNotesUrl);

        _refreshButton = CreateButton(
            TranslationHelper.Get("Update_HistoryRefresh", failsafe: "Refresh"),
            primary: false,
            subtle: false);
        _refreshButton.Click += (_, _) => _ = LoadReleasesAsync();

        _retryButton = CreateButton(
            TranslationHelper.Get("Update_HistoryRetry", failsafe: "Retry"),
            primary: true,
            subtle: false);
        _retryButton.Visibility = Visibility.Collapsed;
        _retryButton.Click += (_, _) => _ = LoadReleasesAsync();

        Content = BuildContent();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
            else if (e.Key == Key.F5)
                _ = LoadReleasesAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
        };
    }

    internal static void ShowWindow(string preferredVersion = null)
    {
        if (_instance is { IsLoaded: true } existing)
        {
            existing.Activate();
            if (!string.IsNullOrWhiteSpace(preferredVersion))
                existing.SelectPreferredVersion(preferredVersion);
            return;
        }

        _instance = new ChangelogWindow(preferredVersion);
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ApplyBackdrop();
        _ = LoadReleasesAsync();
    }

    private FrameworkElement BuildContent()
    {
        var close = CreateButton("\uE711", primary: false, subtle: true);
        close.FontFamily = new FontFamily("Segoe MDL2 Assets");
        close.FontSize = 12;
        close.Width = 30;
        close.Padding = new Thickness(0);
        close.Click += (_, _) => Close();

        var icon = new TextBlock
        {
            Text = FontSymbols.History,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = ThemePalette.Accent(_isDark),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        var title = new TextBlock
        {
            Text = TranslationHelper.Get("Update_HistoryTitle", failsafe: "Update History"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(icon);
        titleRow.Children.Add(title);

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        DockPanel.SetDock(titleRow, Dock.Left);
        header.Children.Add(titleRow);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed)
                return;

            try
            {
                DragMove();
            }
            catch
            {
                // The pointer was released before the drag started.
            }
        };

        var hint = new TextBlock
        {
            Text = TranslationHelper.Get(
                "Update_HistoryHint",
                failsafe: "Browse the release notes from recent stable versions."),
            Foreground = ThemePalette.SecondaryText(_isDark),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var listScroll = new ScrollViewer
        {
            Content = _releaseList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8),
            Background = Brushes.Transparent,
        };
        var releaseCard = new Border
        {
            Background = ThemePalette.ButtonBg(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = listScroll,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var detailsHeader = new StackPanel();
        detailsHeader.Children.Add(_versionText);
        detailsHeader.Children.Add(_dateText);
        var notesCard = new Border
        {
            Background = ThemePalette.ButtonBg(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = _notesViewer,
        };
        var details = new Grid();
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(detailsHeader, 0);
        Grid.SetRow(notesCard, 1);
        details.Children.Add(detailsHeader);
        details.Children.Add(notesCard);

        var splitter = new GridSplitter
        {
            Width = 1,
            Background = ThemePalette.Border(_isDark),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Columns,
            Cursor = Cursors.SizeWE,
        };
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(210),
            MinWidth = 170,
        });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
            MinWidth = 300,
        });
        Grid.SetColumn(releaseCard, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(details, 2);
        body.Children.Add(releaseCard);
        body.Children.Add(splitter);
        body.Children.Add(details);

        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(_retryButton, Dock.Right);
        DockPanel.SetDock(_refreshButton, Dock.Right);
        var closeButton = CreateButton(
            TranslationHelper.Get("Update_HistoryClose", failsafe: "Close"),
            primary: false,
            subtle: false);
        closeButton.IsCancel = true;
        closeButton.Click += (_, _) => Close();
        DockPanel.SetDock(closeButton, Dock.Right);
        DockPanel.SetDock(_githubButton, Dock.Left);
        _retryButton.Margin = new Thickness(8, 0, 0, 0);
        _refreshButton.Margin = new Thickness(8, 0, 0, 0);
        closeButton.Margin = new Thickness(8, 0, 0, 0);
        footer.Children.Add(_githubButton);
        footer.Children.Add(_retryButton);
        footer.Children.Add(_refreshButton);
        footer.Children.Add(closeButton);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(header, 0);
        Grid.SetRow(hint, 1);
        Grid.SetRow(_statusText, 2);
        Grid.SetRow(body, 3);
        Grid.SetRow(footer, 4);
        content.Children.Add(header);
        content.Children.Add(hint);
        content.Children.Add(_statusText);
        content.Children.Add(body);
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
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);
        return panel;
    }

    private async Task LoadReleasesAsync()
    {
        if (_closed)
            return;

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var cancellation = _loadCancellation.Token;

        _statusText.Text = TranslationHelper.Get(
            "Update_HistoryLoading",
            failsafe: "Loading update history...");
        _statusText.Visibility = Visibility.Visible;
        _refreshButton.IsEnabled = false;
        _retryButton.Visibility = Visibility.Collapsed;

        if (_releases.Count == 0)
            _releaseList.Children.Clear();

        try
        {
            var releases = await ReleaseHistoryService.LoadAsync(cancellation);
            if (cancellation.IsCancellationRequested || _closed)
                return;

            _releases = releases;
            PopulateReleaseList();

            if (_releases.Count == 0)
            {
                _statusText.Text = TranslationHelper.Get(
                    "Update_HistoryEmpty",
                    failsafe: "No published release history was found.");
                _notesViewer.Document = EmptyDocument();
                _versionText.Text = string.Empty;
                _dateText.Text = string.Empty;
                _githubButton.IsEnabled = false;
                return;
            }

            _statusText.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // Closing the window or refreshing cancels the previous request.
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Release history failed: {e}");
            _statusText.Text = string.Format(
                TranslationHelper.Get(
                    "Update_HistoryError",
                    failsafe: "Could not load update history: {0}"),
                e.Message);
            _statusText.Visibility = Visibility.Visible;
            _retryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (!_closed &&
                _loadCancellation is not null &&
                _loadCancellation.Token == cancellation)
            {
                _refreshButton.IsEnabled = true;
            }
        }
    }

    private void PopulateReleaseList()
    {
        var selectedVersion = _selectedRelease?.Version ?? _preferredVersion;
        _releaseList.Children.Clear();
        _selectedItem = null;
        _selectedRelease = null;

        Button first = null;
        Button target = null;
        foreach (var release in _releases)
        {
            var item = CreateReleaseItem(release);
            _releaseList.Children.Add(item);
            first ??= item;
            if (!string.IsNullOrWhiteSpace(selectedVersion) &&
                string.Equals(release.Version, NormalizeVersion(selectedVersion), StringComparison.OrdinalIgnoreCase))
            {
                target = item;
            }
        }

        var initial = target ?? first;
        if (initial?.Tag is UpdateReleaseInfo selected)
            SelectRelease(selected, initial);
    }

    private Button CreateReleaseItem(UpdateReleaseInfo release)
    {
        var version = new TextBlock
        {
            Text = IsCurrentVersion(release.Version)
                ? $"{release.Version}  ·  {TranslationHelper.Get("Update_HistoryCurrent", failsafe: "Current")}"
                : release.Version,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var date = new TextBlock
        {
            Text = release.PublishedAt is { } published
                ? published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)
                : string.Empty,
            Foreground = ThemePalette.SecondaryText(_isDark),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
        };
        var labels = new StackPanel { Width = 160 };
        labels.Children.Add(version);
        labels.Children.Add(date);

        var item = CreateButton(string.Empty, primary: false, subtle: false);
        item.Content = labels;
        item.Tag = release;
        item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        item.Padding = new Thickness(10, 7, 10, 7);
        item.Margin = new Thickness(0, 0, 0, 4);
        item.MinWidth = 0;
        item.Click += (_, _) => SelectRelease(release, item);
        AutomationProperties.SetName(item,
            $"{release.Version} {release.PublishedAt?.ToLocalTime():yyyy-MM-dd}");
        return item;
    }

    private void SelectRelease(UpdateReleaseInfo release, Button item)
    {
        if (_selectedItem is not null)
            _selectedItem.Background = Brushes.Transparent;
        item.Background = ThemePalette.AccentTint(_isDark);
        _selectedItem = item;
        _selectedRelease = release;

        _versionText.Text = release.Title;
        var date = release.PublishedAt is { } published
            ? published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)
            : string.Empty;
        if (IsCurrentVersion(release.Version))
        {
            date = string.IsNullOrEmpty(date)
                ? TranslationHelper.Get("Update_HistoryCurrent", failsafe: "Current version")
                : $"{date}  ·  {TranslationHelper.Get("Update_HistoryCurrent", failsafe: "Current version")}";
        }
        _dateText.Text = date;

        var notes = string.IsNullOrWhiteSpace(release.Notes)
            ? TranslationHelper.Get(
                "Update_HistoryNoNotes",
                failsafe: "No release notes were provided for this version.")
            : release.Notes;
        var baseUri = Uri.TryCreate(release.ReleaseNotesUrl, UriKind.Absolute, out var releaseUri)
            ? releaseUri
            : null;
        _notesViewer.Document = ReleaseNotesFormatter.BuildDocument(notes, _isDark, OpenUrl, baseUri);
        _githubButton.IsEnabled = !string.IsNullOrEmpty(release.ReleaseNotesUrl);
    }

    private void SelectPreferredVersion(string version)
    {
        var normalized = NormalizeVersion(version);
        foreach (var child in _releaseList.Children)
        {
            if (child is Button { Tag: UpdateReleaseInfo release } item &&
                string.Equals(release.Version, normalized, StringComparison.OrdinalIgnoreCase))
            {
                SelectRelease(release, item);
                return;
            }
        }
    }

    private static bool IsCurrentVersion(string version)
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version;
        if (current is null || !Version.TryParse(NormalizeVersion(version), out var release))
            return false;

        return current.Major == release.Major &&
               current.Minor == release.Minor &&
               current.Build == Math.Max(0, release.Build);
    }

    private static string NormalizeVersion(string value)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];
        var suffix = value.IndexOfAny(['-', '+']);
        return suffix >= 0 ? value[..suffix] : value;
    }

    private void ApplyBackdrop()
    {
        WindowHelper.DisableDwmBlur(this);
        MenuSurface.Apply(this, _isDark, MenuSurface.SurfaceProminence.Panel);
    }

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
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static Brush ContrastText(Brush background)
    {
        if (background is not SolidColorBrush solid)
            return Brushes.White;

        var luminance = (0.299 * solid.Color.R + 0.587 * solid.Color.G + 0.114 * solid.Color.B) / 255d;
        return luminance > 0.6
            ? new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A))
            : Brushes.White;
    }

    private static FlowDocument EmptyDocument()
    {
        return new FlowDocument(new Paragraph())
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
        };
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No default browser; keep the history window usable.
        }
    }
}
