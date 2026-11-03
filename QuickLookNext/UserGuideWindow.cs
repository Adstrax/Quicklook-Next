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
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLookNext;

/// <summary>
/// A self-contained, searchable user guide. The copy is embedded in the app so
/// opening the guide never starts a network request or loads WebView2.
/// </summary>
internal sealed class UserGuideWindow : Window
{
    private const string IssuesUrl = "https://github.com/Adstrax/Quicklook-Next/issues";
    private const string WidthSetting = "UserGuideWidth";
    private const string HeightSetting = "UserGuideHeight";
    private const string SectionSetting = "UserGuideSectionIndex";

    private static UserGuideWindow _instance;

    private readonly bool _isDark;
    private readonly StackPanel _navigation = new();
    private readonly StackPanel _content = new();
    private readonly ScrollViewer _contentScroll;
    private readonly TextBox _searchBox;
    private readonly TextBlock _searchPlaceholder;
    private readonly TextBlock _titleText;
    private readonly TextBlock _subtitleText;
    private readonly TextBlock _emptyNavigation;
    private readonly Button _chineseButton;
    private readonly Button _englishButton;
    private readonly Button _issuesButton;

    private IReadOnlyList<UserGuideContent.GuideSection> _sections =
        Array.Empty<UserGuideContent.GuideSection>();
    private UserGuideContent.GuideSection _selectedSection;
    private int _sectionIndexToRestore;
    private bool _chinese;
    private bool _rebuildingNavigation;
    private bool _accentApplied;
    private bool _backdropRefreshQueued;
    private bool _closed;
    private ResizeEdge _activeResize;
    private Point _resizeStartScreen;
    private Rect _resizeStartBounds;

    private UserGuideWindow(bool chinese)
    {
        _isDark = TrayIconManager.IsDarkTheme();
        _chinese = chinese;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        // A native resize frame is incompatible with this WCA recipe: WPF draws
        // a second non-client surface over the acrylic. Borderless resizing is
        // handled by the transparent hit targets created in BuildContent.
        ResizeMode = ResizeMode.NoResize;
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(600d, workArea.Width - 40d);
        var maxHeight = Math.Max(480d, workArea.Height - 40d);
        MinWidth = Math.Min(760d, maxWidth);
        MinHeight = Math.Min(520d, maxHeight);
        Width = ClampWindowDimension(
            SettingHelper.Get(WidthSetting, 0d, "QuickLookNext"),
            Math.Min(1040d, workArea.Width - 80d),
            MinWidth,
            maxWidth);
        Height = ClampWindowDimension(
            SettingHelper.Get(HeightSetting, 0d, "QuickLookNext"),
            Math.Min(760d, workArea.Height - 80d),
            MinHeight,
            maxHeight);
        _sectionIndexToRestore = Math.Max(
            0,
            SettingHelper.Get(SectionSetting, 0, "QuickLookNext"));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        UseLayoutRounding = true;
        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));
        FontSize = 13;
        Foreground = ThemePalette.Text(_isDark);
        Background = Brushes.Transparent;

        _titleText = new TextBlock
        {
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _subtitleText = new TextBlock
        {
            FontSize = 11,
            Foreground = ThemePalette.SecondaryText(_isDark),
            Margin = new Thickness(0, 2, 0, 0),
        };

        _searchBox = new TextBox
        {
            Background = ThemePalette.ContentPlate(_isDark),
            Foreground = ThemePalette.Text(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _searchPlaceholder = new TextBlock
        {
            Foreground = ThemePalette.SecondaryText(_isDark),
            Margin = new Thickness(11, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        _emptyNavigation = new TextBlock
        {
            Foreground = ThemePalette.SecondaryText(_isDark),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 12, 10, 0),
            Visibility = Visibility.Collapsed,
        };

        _contentScroll = new ScrollViewer
        {
            Content = _content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(28, 22, 30, 28),
            Background = Brushes.Transparent,
        };

        _chineseButton = CreateButton(string.Empty, primary: false, subtle: true);
        _chineseButton.FontSize = 12;
        _chineseButton.Padding = new Thickness(9, 4, 9, 4);
        _chineseButton.Click += (_, _) => ReloadLanguage(chinese: true);

        _englishButton = CreateButton(string.Empty, primary: false, subtle: true);
        _englishButton.FontSize = 12;
        _englishButton.Padding = new Thickness(9, 4, 9, 4);
        _englishButton.Click += (_, _) => ReloadLanguage(chinese: false);

        _issuesButton = CreateButton(string.Empty, primary: false, subtle: true);
        _issuesButton.FontSize = 12;
        _issuesButton.Padding = new Thickness(8, 4, 8, 4);
        _issuesButton.Foreground = ThemePalette.Accent(_isDark);
        _issuesButton.Click += (_, _) => OpenUrl(IssuesUrl);

        _searchBox.TextChanged += (_, _) =>
        {
            UpdateSearchPlaceholder();
            FilterNavigation(_searchBox.Text);
        };

        Content = BuildContent();
        ReloadLanguage(chinese);

        PreviewKeyDown += OnPreviewKeyDown;
        MouseLeftButtonUp += (_, _) => EndResize(saveSize: true);
        LostMouseCapture += (_, _) => _activeResize = ResizeEdge.None;
        Closed += (_, _) =>
        {
            SaveWindowState();
            _closed = true;
            _instance = null;
        };
    }

    internal static void ShowWindow()
    {
        if (_instance is { IsLoaded: true } existing)
        {
            existing.Activate();
            return;
        }

        var chinese = IsChineseUi();
        _instance = new UserGuideWindow(chinese);
        _instance.Show();
        _instance.Activate();
        _instance._searchBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyBackdrop();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        QueueBackdropRefresh();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        QueueBackdropRefresh();
    }

    private FrameworkElement BuildContent()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader();
        Grid.SetRow(header, 0);

        var body = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(238) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var sidebar = BuildSidebar();
        var separator = new Border
        {
            Width = 1,
            Background = ThemePalette.Separator(_isDark),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var contentSurface = new Border
        {
            Background = ThemePalette.ContentPlate(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _contentScroll,
        };

        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(separator, 1);
        Grid.SetColumn(contentSurface, 2);
        body.Children.Add(sidebar);
        body.Children.Add(separator);
        body.Children.Add(contentSurface);
        Grid.SetRow(body, 1);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);

        root.Children.Add(header);
        root.Children.Add(body);
        root.Children.Add(footer);

        var panel = new Border
        {
            Background = MenuSurface.SurfaceBrush(_isDark, MenuSurface.SurfaceProminence.Menu),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20, 16, 20, 16),
            Child = root,
        };
        KeyboardNavigation.SetTabNavigation(panel, KeyboardNavigationMode.Cycle);

        var frame = new Grid { Background = Brushes.Transparent };
        frame.Children.Add(panel);
        AddResizeTargets(frame);
        return frame;
    }

    private FrameworkElement BuildHeader()
    {
        var close = CreateButton("\uE711", primary: false, subtle: true);
        close.FontFamily = new FontFamily("Segoe MDL2 Assets");
        close.FontSize = 12;
        close.Width = 30;
        close.Padding = new Thickness(0);
        close.Click += (_, _) => Close();

        var icon = new TextBlock
        {
            Text = FontSymbols.Help,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = ThemePalette.Accent(_isDark),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 11, 0),
        };

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(_titleText);
        titleStack.Children.Add(_subtitleText);

        var languageActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        _chineseButton.Margin = new Thickness(0, 0, 4, 0);
        languageActions.Children.Add(_chineseButton);
        languageActions.Children.Add(_englishButton);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(languageActions);
        actions.Children.Add(close);

        var header = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(icon);
        titleRow.Children.Add(titleStack);
        DockPanel.SetDock(titleRow, Dock.Left);
        header.Children.Add(titleRow);

        titleRow.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed)
                return;

            try
            {
                DragMove();
            }
            catch
            {
                // The mouse button was released before the drag began.
            }
        };

        return header;
    }

    private FrameworkElement BuildSidebar()
    {
        var searchHost = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        searchHost.Children.Add(_searchBox);
        searchHost.Children.Add(_searchPlaceholder);

        var navScroll = new ScrollViewer
        {
            Content = _navigation,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
        };

        var sidebarContent = new Grid();
        sidebarContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        sidebarContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        sidebarContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(searchHost, 0);
        Grid.SetRow(navScroll, 1);
        Grid.SetRow(_emptyNavigation, 2);
        sidebarContent.Children.Add(searchHost);
        sidebarContent.Children.Add(navScroll);
        sidebarContent.Children.Add(_emptyNavigation);

        return new Border
        {
            Background = ThemePalette.ButtonBg(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 10, 10, 10),
            Child = sidebarContent,
        };
    }

    private FrameworkElement BuildFooter()
    {
        var version = new TextBlock
        {
            Name = "VersionText",
            Text = $"v{typeof(UserGuideWindow).Assembly.GetName().Version?.ToString(3) ?? string.Empty}",
            FontSize = 11,
            Foreground = ThemePalette.SecondaryText(_isDark),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(_issuesButton, Dock.Left);
        DockPanel.SetDock(version, Dock.Right);
        footer.Children.Add(_issuesButton);
        footer.Children.Add(version);
        return footer;
    }

    private void ReloadLanguage(bool chinese)
    {
        var selectedIndex = IndexOfSection(_selectedSection);
        if (selectedIndex >= 0)
            _sectionIndexToRestore = selectedIndex;

        _chinese = chinese;
        _sections = UserGuideContent.Create(_chinese);
        _sectionIndexToRestore = Math.Clamp(
            _sectionIndexToRestore,
            0,
            Math.Max(0, _sections.Count - 1));
        _selectedSection = _sections.ElementAtOrDefault(_sectionIndexToRestore);

        _titleText.Text = L("User Guide", "使用指南");
        _subtitleText.Text = L(
            "Features, controls, and troubleshooting",
            "功能、操作方式和常见问题");
        Title = _titleText.Text;
        _chineseButton.Content = "中文";
        _englishButton.Content = "English";
        AutomationProperties.SetName(_chineseButton, L("Chinese", "中文"));
        AutomationProperties.SetName(_englishButton, "English");
        _searchPlaceholder.Text = L("Search this guide", "搜索使用指南");
        _searchBox.Clear();
        UpdateSearchPlaceholder();

        _issuesButton.Content = L("GitHub Issues  \u2197", "GitHub 问题反馈  \u2197");
        AutomationProperties.SetName(
            _issuesButton,
            L("Open GitHub Issues", "打开 GitHub 问题反馈"));

        UpdateLanguageButtonVisuals();
        FilterNavigation(string.Empty, preserveSelection: true);
        RenderSelectedSection();
    }

    private void FilterNavigation(string query, bool preserveSelection = false)
    {
        if (_rebuildingNavigation)
            return;

        query = query?.Trim() ?? string.Empty;
        var previous = preserveSelection ? _selectedSection : null;
        IReadOnlyList<UserGuideContent.GuideSection> matches = string.IsNullOrEmpty(query)
            ? _sections
            : _sections.Where(section =>
                section.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();

        _rebuildingNavigation = true;
        _navigation.Children.Clear();
        _emptyNavigation.Text = L(
            $"No sections match \u201c{query}\u201d.",
            $"没有匹配“{query}”的章节。");
        _emptyNavigation.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var section in matches)
            _navigation.Children.Add(CreateNavigationButton(section));

        _rebuildingNavigation = false;

        var nextSelection = previous is not null && matches.Contains(previous)
            ? previous
            : matches.FirstOrDefault();
        if (nextSelection is not null && !ReferenceEquals(nextSelection, _selectedSection))
        {
            _selectedSection = nextSelection;
            RenderSelectedSection();
        }

        UpdateNavigationSelection();
    }

    private Button CreateNavigationButton(UserGuideContent.GuideSection section)
    {
        var button = CreateButton(section.Title, primary: false, subtle: false);
        button.Tag = section;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.MinWidth = 0;
        button.Padding = new Thickness(10, 8, 10, 8);
        button.Margin = new Thickness(0, 0, 0, 3);
        button.FontWeight = FontWeights.Normal;
        button.Click += (_, _) =>
        {
            _selectedSection = section;
            _sectionIndexToRestore = IndexOfSection(section);
            UpdateNavigationSelection();
            RenderSelectedSection();
            _contentScroll.ScrollToTop();
        };
        return button;
    }

    private void UpdateNavigationSelection()
    {
        foreach (var child in _navigation.Children)
        {
            if (child is not Button button)
                continue;

            var selected = ReferenceEquals(button.Tag, _selectedSection);
            button.Background = selected
                ? ThemePalette.AccentTint(_isDark)
                : Brushes.Transparent;
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private void RenderSelectedSection()
    {
        _content.Children.Clear();
        if (_selectedSection is null)
        {
            _content.Children.Add(CreateBodyText(
                L("Select a section to begin.", "请选择一个章节开始阅读。"),
                ThemePalette.SecondaryText(_isDark)));
            return;
        }

        var eyebrow = new TextBlock
        {
            Text = L("QUICKLOOK NEXT  /  USER GUIDE", "QUICKLOOK NEXT  /  使用指南"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemePalette.Accent(_isDark),
            Margin = new Thickness(0, 0, 0, 8),
        };
        _content.Children.Add(eyebrow);

        _content.Children.Add(new TextBlock
        {
            Text = _selectedSection.Title,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        _content.Children.Add(new TextBlock
        {
            Text = _selectedSection.Summary,
            FontSize = 14,
            LineHeight = 21,
            Foreground = ThemePalette.SecondaryText(_isDark),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18),
        });

        foreach (var block in _selectedSection.Blocks)
            _content.Children.Add(RenderBlock(block));
    }

    private FrameworkElement RenderBlock(UserGuideContent.GuideBlock block)
    {
        switch (block)
        {
            case UserGuideContent.GuideParagraph paragraph:
                return Paragraph(paragraph.Text);

            case UserGuideContent.GuideBullets bullets:
                return BulletList(bullets.Items);

            case UserGuideContent.GuideCallout callout:
                return Callout(callout.Text);

            case UserGuideContent.GuideDefinitions definitions:
                return DefinitionList(definitions.Items);

            case UserGuideContent.GuideShortcuts shortcuts:
                return ShortcutTable(shortcuts.Items);

            default:
                return new FrameworkElement();
        }
    }

    private FrameworkElement Paragraph(string text)
        => CreateBodyText(text, ThemePalette.Text(_isDark), new Thickness(0, 0, 0, 14));

    private FrameworkElement BulletList(IReadOnlyList<string> items)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var item in items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = "\u2022",
                Foreground = ThemePalette.Accent(_isDark),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
            };
            var text = CreateBodyText(item, ThemePalette.Text(_isDark));
            Grid.SetColumn(marker, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(marker);
            row.Children.Add(text);
            panel.Children.Add(row);
        }
        return panel;
    }

    private FrameworkElement Callout(string text)
    {
        var label = CreateBodyText(text, ThemePalette.Text(_isDark));
        label.FontWeight = FontWeights.SemiBold;
        label.LineHeight = 20;
        return new Border
        {
            Background = ThemePalette.AccentTint(_isDark),
            BorderBrush = ThemePalette.Accent(_isDark),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(13, 10, 13, 10),
            Margin = new Thickness(0, 3, 0, 16),
            Child = label,
        };
    }

    private FrameworkElement DefinitionList(IReadOnlyList<(string Title, string Body)> items)
    {
        var panel = new StackPanel();
        foreach (var item in items)
            panel.Children.Add(Definition(item.Title, item.Body));
        return panel;
    }

    private FrameworkElement Definition(string title, string body)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        stack.Children.Add(CreateBodyText(body, ThemePalette.SecondaryText(_isDark)));

        return new Border
        {
            Background = ThemePalette.ButtonBg(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(13, 10, 13, 11),
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
        };
    }

    private FrameworkElement ShortcutTable(IReadOnlyList<(string Input, string Action)> items)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var input = new Border
            {
                Background = ThemePalette.ButtonBg(_isDark),
                BorderBrush = ThemePalette.Border(_isDark),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    Text = item.Input,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                },
            };
            var action = CreateBodyText(item.Action, ThemePalette.SecondaryText(_isDark));
            action.Margin = new Thickness(0, 4, 0, 7);
            Grid.SetRow(input, index);
            Grid.SetRow(action, index);
            Grid.SetColumn(input, 0);
            Grid.SetColumn(action, 1);
            grid.Children.Add(input);
            grid.Children.Add(action);
        }

        return new Border
        {
            Background = ThemePalette.ContentPlate(_isDark),
            BorderBrush = ThemePalette.Border(_isDark),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(14, 12, 14, 7),
            Child = grid,
        };
    }

    private TextBlock CreateBodyText(string text, Brush foreground, Thickness? margin = null)
        => new()
        {
            Text = text,
            Foreground = foreground,
            FontSize = 13,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            Margin = margin ?? new Thickness(0),
        };

    private void UpdateLanguageButtonVisuals()
    {
        SetLanguageButtonState(_chineseButton, _chinese);
        SetLanguageButtonState(_englishButton, !_chinese);
    }

    private void SetLanguageButtonState(Button button, bool selected)
    {
        button.Background = selected ? ThemePalette.AccentTint(_isDark) : Brushes.Transparent;
        button.Foreground = selected ? ThemePalette.Accent(_isDark) : ThemePalette.Text(_isDark);
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void UpdateSearchPlaceholder()
        => _searchPlaceholder.Visibility = string.IsNullOrEmpty(_searchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _searchBox.Focus();
            _searchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
            return;

        if (!string.IsNullOrEmpty(_searchBox.Text))
        {
            _searchBox.Clear();
            _searchBox.Focus();
            e.Handled = true;
            return;
        }

        Close();
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

    private void QueueBackdropRefresh()
    {
        if (_backdropRefreshQueued || _closed)
            return;

        _backdropRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _backdropRefreshQueued = false;
            ApplyBackdrop();
        }), DispatcherPriority.Loaded);
    }

    private void ApplyBackdrop()
    {
        if (_closed)
            return;

        WindowHelper.DisableDwmBlur(this);
        _accentApplied = MenuSurface.Apply(this, _isDark, MenuSurface.SurfaceProminence.Menu);
    }

    internal string DiagnoseBackdrop()
        => $"accent-applied={_accentApplied} {MenuSurface.Diagnose()}";

    private string L(string english, string chinese) => _chinese ? chinese : english;

    private int IndexOfSection(UserGuideContent.GuideSection section)
    {
        if (section is null)
            return -1;

        for (var index = 0; index < _sections.Count; index++)
        {
            if (ReferenceEquals(_sections[index], section))
                return index;
        }

        return -1;
    }

    private void SaveWindowState()
    {
        try
        {
            if (WindowState == WindowState.Normal)
            {
                SettingHelper.Set(WidthSetting, Math.Round(ActualWidth, 2), "QuickLookNext");
                SettingHelper.Set(HeightSetting, Math.Round(ActualHeight, 2), "QuickLookNext");
            }

            var sectionIndex = IndexOfSection(_selectedSection);
            if (sectionIndex >= 0)
                SettingHelper.Set(SectionSetting, sectionIndex, "QuickLookNext");
        }
        catch
        {
            // Remembering window state must never prevent the guide from closing.
        }
    }

    private static double ClampWindowDimension(
        double stored,
        double preferred,
        double minimum,
        double maximum)
    {
        var value = double.IsFinite(stored) && stored > 0d ? stored : preferred;
        return Math.Clamp(value, minimum, maximum);
    }

    private void AddResizeTargets(Grid frame)
    {
        const double edge = 6d;
        const double corner = 12d;

        AddResizeTarget(
            frame, ResizeEdge.Top, Cursors.SizeNS,
            HorizontalAlignment.Stretch, VerticalAlignment.Top, 0d, 0d, double.NaN, edge);
        AddResizeTarget(
            frame, ResizeEdge.Bottom, Cursors.SizeNS,
            HorizontalAlignment.Stretch, VerticalAlignment.Bottom, 0d, 0d, double.NaN, edge);
        AddResizeTarget(
            frame, ResizeEdge.Left, Cursors.SizeWE,
            HorizontalAlignment.Left, VerticalAlignment.Stretch, 0d, 0d, edge, double.NaN);
        AddResizeTarget(
            frame, ResizeEdge.Right, Cursors.SizeWE,
            HorizontalAlignment.Right, VerticalAlignment.Stretch, 0d, 0d, edge, double.NaN);
        AddResizeTarget(
            frame, ResizeEdge.Left | ResizeEdge.Top, Cursors.SizeNWSE,
            HorizontalAlignment.Left, VerticalAlignment.Top, 0d, 0d, corner, corner);
        AddResizeTarget(
            frame, ResizeEdge.Right | ResizeEdge.Top, Cursors.SizeNESW,
            HorizontalAlignment.Right, VerticalAlignment.Top, 0d, 0d, corner, corner);
        AddResizeTarget(
            frame, ResizeEdge.Left | ResizeEdge.Bottom, Cursors.SizeNESW,
            HorizontalAlignment.Left, VerticalAlignment.Bottom, 0d, 0d, corner, corner);
        AddResizeTarget(
            frame, ResizeEdge.Right | ResizeEdge.Bottom, Cursors.SizeNWSE,
            HorizontalAlignment.Right, VerticalAlignment.Bottom, 0d, 0d, corner, corner);
    }

    private void AddResizeTarget(
        Grid frame,
        ResizeEdge edge,
        Cursor cursor,
        HorizontalAlignment horizontal,
        VerticalAlignment vertical,
        double marginLeft,
        double marginTop,
        double width,
        double height)
    {
        var target = new Border
        {
            Background = Brushes.Transparent,
            Cursor = cursor,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Width = width,
            Height = height,
            Margin = new Thickness(marginLeft, marginTop, 0d, 0d),
            Tag = edge,
        };
        Panel.SetZIndex(target, 100);
        target.MouseLeftButtonDown += (_, e) =>
        {
            BeginResize(edge, e);
            e.Handled = true;
        };
        frame.Children.Add(target);
    }

    private void BeginResize(ResizeEdge edge, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        _activeResize = edge;
        _resizeStartScreen = PointToScreen(e.GetPosition(this));
        _resizeStartBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_activeResize == ResizeEdge.None || e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentScreen = PointToScreen(e.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        var dx = (currentScreen.X - _resizeStartScreen.X) / dpi.DpiScaleX;
        var dy = (currentScreen.Y - _resizeStartScreen.Y) / dpi.DpiScaleY;
        var start = _resizeStartBounds;
        var left = start.Left;
        var top = start.Top;
        var width = start.Width;
        var height = start.Height;

        if (_activeResize.HasFlag(ResizeEdge.Left))
        {
            left = start.Left + dx;
            width = start.Width - dx;
        }
        else if (_activeResize.HasFlag(ResizeEdge.Right))
        {
            width = start.Width + dx;
        }

        if (_activeResize.HasFlag(ResizeEdge.Top))
        {
            top = start.Top + dy;
            height = start.Height - dy;
        }
        else if (_activeResize.HasFlag(ResizeEdge.Bottom))
        {
            height = start.Height + dy;
        }

        if (width < MinWidth)
        {
            if (_activeResize.HasFlag(ResizeEdge.Left))
                left = start.Right - MinWidth;
            width = MinWidth;
        }

        if (height < MinHeight)
        {
            if (_activeResize.HasFlag(ResizeEdge.Top))
                top = start.Bottom - MinHeight;
            height = MinHeight;
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private void EndResize(bool saveSize)
    {
        if (_activeResize == ResizeEdge.None)
            return;

        _activeResize = ResizeEdge.None;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        if (saveSize)
            SaveWindowState();
    }

    [Flags]
    private enum ResizeEdge
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
    }

    private static bool IsChineseUi()
    {
        var overrideName = SettingHelper.Get("Language", string.Empty, "QuickLookNext");
        if (!string.IsNullOrWhiteSpace(overrideName))
            return overrideName.TrimStart().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        return System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase);
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
            // Keep the guide usable if no default browser is configured.
        }
    }
}
