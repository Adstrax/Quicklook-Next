// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using QuickLook.Common.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

public partial class ArchiveFileListView : UserControl, IDisposable
{
    private const string SettingsDomain = "QuickLook.Plugin.ArchiveViewer";
    private const string FileListRatioSetting = "ArchiveSidebarRatio";
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(180);

    private readonly DispatcherTimer _searchDelayTimer;
    private ArchiveFileEntry _archiveRoot;
    private ArchiveFileEntry _selectedEntry;
    private ArchiveFileEntry _selectionPreviewAlreadyRequested;
    private IEnumerable<ArchiveFileEntry> _unfilteredItems;
    private List<ArchiveFileEntry> _visibleSearchResults;
    private List<ArchiveFileEntry> _searchMatches;
    private string _completedSearchQuery;
    private SearchViewState _searchState;
    private CancellationTokenSource _searchCancellation;
    private int _activeSearchIndex = -1;
    private long _searchVersion;
    private bool _suppressSearchTextChanged;
    private volatile bool _disposed;

    public event EventHandler<ArchiveFileSelectedEventArgs> PreviewRequested;
    public event EventHandler<ArchiveFileSelectedEventArgs> SelectionPreviewRequested;

    public ArchiveFileListView()
    {
        _searchDelayTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SearchDelay
        };

        InitializeComponent();

        _searchDelayTimer.Tick += SearchDelayTimer_Tick;
        Loaded += ArchiveFileListView_Loaded;
        PreviewKeyDown += ArchiveFileListView_PreviewKeyDown;

        SetLocalizedTexts();
        RefreshSearchActionState();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _disposed = true;
        Loaded -= ArchiveFileListView_Loaded;
        _searchDelayTimer.Stop();
        _searchDelayTimer.Tick -= SearchDelayTimer_Tick;
        PreviewKeyDown -= ArchiveFileListView_PreviewKeyDown;
        CancelSearch();
        archiveSearchBox.TextChanged -= ArchiveSearchBox_TextChanged;
        IconManager.ClearCache();
    }

    public void SetArchiveName(string archiveName)
    {
        archiveNameText.Text = archiveName ?? string.Empty;
        archiveNameText.ToolTip = archiveName;
    }

    public void SetArchiveSummary(
        string count,
        string compressedSize,
        string uncompressedSize)
    {
        archiveCountText.Text = count ?? string.Empty;
        archiveCountText.ToolTip = count;
        archiveSizeCText.Text = compressedSize ?? string.Empty;
        archiveSizeCText.ToolTip = compressedSize;
        archiveSizeUText.Text = uncompressedSize ?? string.Empty;
        archiveSizeUText.ToolTip = uncompressedSize;
    }

    public void SetDataContext(ArchiveFileEntry root)
    {
        CancelSearch();
        _searchState = null;
        _visibleSearchResults = null;
        _searchMatches = null;
        _completedSearchQuery = null;
        _activeSearchIndex = -1;
        _archiveRoot = root;
        _selectedEntry = root;
        _unfilteredItems = root == null ? [] : [root];

        treeView.ItemsSource = _unfilteredItems;
        ExpandAllFolders(_archiveRoot);
        ClearSearchStatus();
        RefreshSearchActionState();
        ScheduleTreeSelection(root, verticalOffset: -1);
    }

    public void ShowPreviewStatus(string title, string message, bool showProgress)
    {
        previewTitleText.Text = title ?? string.Empty;
        previewTitleText.ToolTip = title;
        previewStatusText.Text = message ?? string.Empty;
        previewProgress.Visibility = showProgress
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!showProgress)
            previewContent.Content = null;
        previewStatusPanel.Visibility = Visibility.Visible;
    }

    public void ShowPreviewContent(string title, object content)
    {
        if (content == null)
        {
            ShowPreviewStatus(
                title,
                Translate("No preview is available for this file.", "此文件暂无可用的内容预览。"),
                showProgress: false);
            return;
        }

        previewTitleText.Text = title ?? string.Empty;
        previewTitleText.ToolTip = title;
        previewContent.Content = content;
        previewStatusPanel.Visibility = Visibility.Collapsed;
    }

    public void SetPreviewContent(object content)
    {
        previewContent.Content = content;
    }

    private void ArchiveFileListView_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyFileListRatio(SettingHelper.Get(
            FileListRatioSetting,
            ArchiveSplitLayout.DefaultRatio,
            SettingsDomain));
    }

    private void SetLocalizedTexts()
    {
        var chinese = IsChineseUi;

        archiveSearchHint.Text = chinese ? "搜索文件和路径" : "Search files or paths";
        archiveSearchStatus.Text = string.Empty;
        archiveSearchBox.ToolTip = chinese
            ? "按文件名或路径搜索；多个关键词用空格分隔。Esc 清空。"
            : "Search by file name or path. Separate multiple terms with spaces. Press Esc to clear.";
        previewTitleText.Text = chinese ? "预览" : "Preview";
        previewStatusText.Text = chinese
            ? "在左侧选择文件即可预览。双击文件或按 Enter 打开独立窗口。"
            : "Select a file on the left to preview it. Double-click a file or press Enter to open a separate window.";
        archivePreviousButton.ToolTip = chinese
            ? "上一个匹配项（Shift+F3）"
            : "Previous match (Shift+F3)";
        archiveNextButton.ToolTip = chinese
            ? "下一个匹配项（F3）"
            : "Next match (F3)";
        archiveClearButton.ToolTip = chinese
            ? "清空搜索并恢复之前的展开、选中和滚动位置"
            : "Clear search and restore the previous selection and scroll position";

        AutomationProperties.SetName(
            archiveSearchBox,
            chinese ? "搜索压缩包内容" : "Search archive contents");
        AutomationProperties.SetName(
            treeView,
            chinese ? "压缩包文件导航" : "Archive file navigation");
        AutomationProperties.SetName(
            archivePreviousButton,
            chinese ? "上一个匹配项" : "Previous match");
        AutomationProperties.SetName(
            archiveNextButton,
            chinese ? "下一个匹配项" : "Next match");
        AutomationProperties.SetName(
            archiveClearButton,
            chinese ? "清空搜索" : "Clear search");
    }

    private static bool IsChineseUi =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase);

    private void TreeView_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        var entry = e.NewValue as ArchiveFileEntry;
        if (entry == null)
            return;

        _selectedEntry = entry;
        UpdateSearchSelection(entry);

        if (entry.IsFolder)
            return;

        if (ReferenceEquals(_selectionPreviewAlreadyRequested, entry))
        {
            _selectionPreviewAlreadyRequested = null;
            return;
        }

        SelectionPreviewRequested?.Invoke(
            this,
            new ArchiveFileSelectedEventArgs(entry));
    }

    private void UpdateSearchSelection(ArchiveFileEntry entry)
    {
        if (_searchMatches == null || _searchMatches.Count == 0)
            return;

        var index = _searchMatches.IndexOf(entry);
        if (index < 0)
        {
            _activeSearchIndex = -1;
            SetSearchStatus(FormatMatchCount(_searchMatches.Count));
            return;
        }

        _activeSearchIndex = index;
        SetSearchStatus(ArchiveSearchPresentation.FormatMatchPosition(
            index,
            _searchMatches.Count), entry.DisplayPath);
    }

    private void TreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            FindVisualParent<ToggleButton>(source) != null ||
            FindVisualParent<TreeViewItem>(source) is not { } item ||
            item.DataContext is not ArchiveFileEntry entry)
        {
            return;
        }

        if (entry.IsFolder)
        {
            entry.IsExpanded = !entry.IsExpanded;
        }
        else
        {
            MarkSelectionPreviewRequested(entry);
            PreviewRequested?.Invoke(
                this,
                new ArchiveFileSelectedEventArgs(entry));
        }

        e.Handled = true;
    }

    private void TreeView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && treeView.SelectedItem is ArchiveFileEntry entry)
        {
            if (entry.IsFolder)
            {
                entry.IsExpanded = !entry.IsExpanded;
            }
            else
            {
                MarkSelectionPreviewRequested(entry);
                PreviewRequested?.Invoke(
                    this,
                    new ArchiveFileSelectedEventArgs(entry));
            }

            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Home or Key.End) ||
            Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        var direction = e.Key == Key.Home
            ? FocusNavigationDirection.First
            : FocusNavigationDirection.Last;
        if (treeView.MoveFocus(new TraversalRequest(direction)))
            e.Handled = true;
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var ratio = ArchiveSplitLayout.RatioFromWidths(
            fileListColumn.ActualWidth,
            previewColumn.ActualWidth);
        ApplyFileListRatio(ratio);
        SettingHelper.Set(FileListRatioSetting, ratio, SettingsDomain);
    }

    private void ApplyFileListRatio(double ratio)
    {
        var normalized = ArchiveSplitLayout.Normalize(ratio);
        fileListColumn.Width = new GridLength(normalized, GridUnitType.Star);
        previewColumn.Width = new GridLength(1 - normalized, GridUnitType.Star);
    }

    private void ArchiveSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSearchTextChanged)
            return;

        _visibleSearchResults = null;
        _searchMatches = null;
        _completedSearchQuery = null;
        _activeSearchIndex = -1;
        archiveSearchHint.Visibility = string.IsNullOrEmpty(archiveSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearSearchStatus();
        RefreshSearchActionState();

        _searchDelayTimer.Stop();
        CancelSearch();
        if (string.IsNullOrWhiteSpace(archiveSearchBox.Text))
        {
            var restoreState = _searchState;
            _searchState = null;
            ApplyUnfilteredItems(restoreState);
            return;
        }

        _searchState ??= CaptureSearchViewState();
        _searchDelayTimer.Start();
    }

    private async void ArchiveSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusSearchBox();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && !string.IsNullOrEmpty(archiveSearchBox.Text))
        {
            ClearSearchAndRestore();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        var query = archiveSearchBox.Text.Trim();
        if (query.Length == 0)
            return;

        e.Handled = true;
        _searchDelayTimer.Stop();
        if (_visibleSearchResults != null &&
            string.Equals(_completedSearchQuery, query, StringComparison.Ordinal))
        {
            OpenFirstMatch();
            return;
        }

        CancelSearch();
        await RunSearchAsync(query, openFirstMatch: true);
    }

    private async void SearchDelayTimer_Tick(object sender, EventArgs e)
    {
        _searchDelayTimer.Stop();
        var query = archiveSearchBox.Text.Trim();
        if (query.Length == 0)
        {
            var restoreState = _searchState;
            _searchState = null;
            ApplyUnfilteredItems(restoreState);
            return;
        }

        await RunSearchAsync(query, openFirstMatch: false);
    }

    private async Task RunSearchAsync(string query, bool openFirstMatch)
    {
        var version = ++_searchVersion;
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        SetSearchStatus(Translate("Searching ...", "搜索中…"));
        RefreshSearchActionState();

        try
        {
            var items = _unfilteredItems ?? [];
            var results = await Task.Run(
                () => ArchiveTreeSearch.Filter(items, query, cancellation.Token),
                cancellation.Token);

            if (_disposed || version != _searchVersion)
                return;

            _visibleSearchResults = results;
            _searchMatches = ArchiveTreeSearch.GetNavigationMatches(results);
            _completedSearchQuery = query;
            _activeSearchIndex = -1;
            treeView.ItemsSource = results;
            ExpandSearchBranches();

            if (_searchMatches.Count == 0)
                SetSearchStatus(Translate("No matches", "无匹配项"));
            else
                SetSearchStatus(FormatMatchCount(_searchMatches.Count));
            RefreshSearchActionState();

            if (openFirstMatch)
                OpenFirstMatch();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ProcessHelper.WriteLog(ex.ToString());
            if (!_disposed && version == _searchVersion)
            {
                SetSearchStatus(Translate("Search failed", "搜索失败"));
                RefreshSearchActionState();
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
                _searchCancellation = null;

            cancellation.Dispose();
        }
    }

    private SearchViewState CaptureSearchViewState()
    {
        return new SearchViewState(
            _selectedEntry,
            FindScrollViewer(treeView)?.VerticalOffset ?? 0d);
    }

    private void ApplyUnfilteredItems(SearchViewState restoreState)
    {
        if (_unfilteredItems == null)
            return;

        ++_searchVersion;
        var entry = restoreState?.Selected ?? _archiveRoot;
        treeView.ItemsSource = _unfilteredItems;
        ExpandAllFolders(_archiveRoot);
        ScheduleTreeSelection(entry, restoreState?.VerticalOffset ?? -1d);
    }

    private void ScheduleTreeSelection(ArchiveFileEntry entry, double verticalOffset)
    {
        if (entry == null)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_disposed)
                return;

            RevealTreeEntry(entry);
            treeView.UpdateLayout();
            if (verticalOffset >= 0d)
            {
                FindScrollViewer(treeView)?.ScrollToVerticalOffset(verticalOffset);
            }
        }));
    }

    private void RevealTreeEntry(ArchiveFileEntry entry)
    {
        if (entry == null)
            return;

        ExpandFolderChain(entry.Parent);
        _selectedEntry = entry;
        entry.IsSelected = true;

        if (FindContainer(entry) is not { } container)
            return;

        container.IsSelected = true;
        container.BringIntoView();
    }

    private void CancelSearch()
    {
        ++_searchVersion;
        var cancellation = _searchCancellation;
        _searchCancellation = null;
        cancellation?.Cancel();
    }

    private void SetSearchStatus(string text, string path = null)
    {
        archiveSearchStatus.Text = text;
        archiveSearchStatus.ToolTip = path;
        archiveSearchStatus.Visibility = Visibility.Visible;
        AutomationProperties.SetName(archiveSearchStatus, text);
        AutomationProperties.SetHelpText(archiveSearchStatus, path ?? string.Empty);
    }

    private void ClearSearchStatus()
    {
        archiveSearchStatus.Text = string.Empty;
        archiveSearchStatus.ToolTip = null;
        archiveSearchStatus.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(archiveSearchStatus, string.Empty);
        AutomationProperties.SetHelpText(archiveSearchStatus, string.Empty);
    }

    private void RefreshSearchActionState()
    {
        var hasMatches = _searchMatches is { Count: > 0 };
        archivePreviousButton.IsEnabled = hasMatches;
        archiveNextButton.IsEnabled = hasMatches;
        archiveClearButton.IsEnabled = !string.IsNullOrEmpty(archiveSearchBox.Text);
    }

    private void ClearSearchAndRestore()
    {
        if (string.IsNullOrEmpty(archiveSearchBox.Text))
            return;

        _suppressSearchTextChanged = true;
        archiveSearchBox.Clear();
        _suppressSearchTextChanged = false;

        var restoreState = _searchState;
        _searchState = null;
        _visibleSearchResults = null;
        _searchMatches = null;
        _completedSearchQuery = null;
        _activeSearchIndex = -1;
        CancelSearch();
        ClearSearchStatus();
        RefreshSearchActionState();
        ApplyUnfilteredItems(restoreState);
        FocusSearchBox();
    }

    private static string Translate(string english, string chinese)
        => IsChineseUi ? chinese : english;

    private static string FormatMatchCount(int count)
    {
        if (IsChineseUi)
            return $"{count} 个匹配项";

        return count == 1 ? "1 match" : $"{count} matches";
    }

    private void ArchiveFileListView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F3 && !string.IsNullOrWhiteSpace(archiveSearchBox.Text))
        {
            MoveSearchMatch(
                Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        FocusSearchBox();
        e.Handled = true;
    }

    private void FocusSearchBox()
    {
        archiveSearchBox.Focus();
        archiveSearchBox.SelectAll();
    }

    private void OpenFirstMatch()
    {
        OpenSearchMatch(0, openFullPreview: true);
    }

    private void ArchivePreviousButton_Click(object sender, RoutedEventArgs e)
        => MoveSearchMatch(-1);

    private void ArchiveNextButton_Click(object sender, RoutedEventArgs e)
        => MoveSearchMatch(1);

    private void ArchiveClearButton_Click(object sender, RoutedEventArgs e)
        => ClearSearchAndRestore();

    private void MoveSearchMatch(int direction)
    {
        if (_searchMatches == null || _searchMatches.Count == 0)
        {
            var query = archiveSearchBox.Text.Trim();
            if (query.Length > 0)
            {
                _searchDelayTimer.Stop();
                CancelSearch();
                _ = RunSearchAsync(query, openFirstMatch: true);
            }

            return;
        }

        var currentIndex = _activeSearchIndex;
        if (currentIndex < 0 || currentIndex >= _searchMatches.Count)
            currentIndex = _searchMatches.IndexOf(_selectedEntry);

        var nextIndex = ArchiveSearchPresentation.GetNextMatchIndex(
            currentIndex,
            _searchMatches.Count,
            direction);
        OpenSearchMatch(nextIndex, openFullPreview: false);
    }

    private void OpenSearchMatch(int index, bool openFullPreview)
    {
        if (_searchMatches == null ||
            index < 0 ||
            index >= _searchMatches.Count)
        {
            return;
        }

        var target = _searchMatches[index];
        _activeSearchIndex = index;
        SetSearchStatus(ArchiveSearchPresentation.FormatMatchPosition(
            index,
            _searchMatches.Count), target.DisplayPath);

        ScheduleTreeSelection(target, verticalOffset: -1d);

        if (target.IsFolder)
            return;

        MarkSelectionPreviewRequested(target);
        var args = new ArchiveFileSelectedEventArgs(target);
        if (openFullPreview)
            PreviewRequested?.Invoke(this, args);
        else
            SelectionPreviewRequested?.Invoke(this, args);
    }

    private void MarkSelectionPreviewRequested(ArchiveFileEntry entry)
    {
        _selectionPreviewAlreadyRequested = entry;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (ReferenceEquals(_selectionPreviewAlreadyRequested, entry))
                _selectionPreviewAlreadyRequested = null;
        }));
    }

    private void ExpandSearchBranches()
    {
        if (_visibleSearchResults == null)
            return;

        foreach (var entry in _visibleSearchResults)
            ExpandFolderChain(entry);
    }

    private static void ExpandFolderChain(ArchiveFileEntry entry)
    {
        while (entry != null)
        {
            if (entry.IsFolder)
                entry.IsExpanded = true;
            entry = entry.Parent;
        }
    }

    internal static void ExpandAllFolders(ArchiveFileEntry root)
    {
        if (root == null)
            return;

        var pending = new Stack<ArchiveFileEntry>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var entry = pending.Pop();
            if (!entry.IsFolder)
                continue;

            entry.IsExpanded = true;
            foreach (var child in entry.Folders)
                pending.Push(child);
        }
    }

    private TreeViewItem FindContainer(ArchiveFileEntry entry)
    {
        if (entry == null)
            return null;

        var parents = new Stack<ArchiveFileEntry>();
        for (var current = entry; current != null; current = current.Parent)
            parents.Push(current);

        ItemsControl owner = treeView;
        TreeViewItem container = null;
        while (parents.Count > 0)
        {
            container = owner.ItemContainerGenerator.ContainerFromItem(parents.Pop()) as TreeViewItem;
            if (container == null)
                return null;

            owner = container;
        }

        return container;
    }

    private static T FindVisualParent<T>(DependencyObject source)
        where T : DependencyObject
    {
        for (var current = source; current != null;)
        {
            if (current is T match)
                return match;

            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static ScrollViewer FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scrollViewer)
            return scrollViewer;

        if (parent is not Visual)
            return null;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(parent, i));
            if (result != null)
                return result;
        }

        return null;
    }

    private sealed record SearchViewState(
        ArchiveFileEntry Selected,
        double VerticalOffset);
}

public sealed class ArchiveFileSelectedEventArgs(ArchiveFileEntry entry) : EventArgs
{
    public ArchiveFileEntry Entry { get; } = entry;
}
