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

using QuickLook.Plugin.ArchiveViewer.ArchiveFile;
using QuickLook.Common.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

public partial class ArchiveFileListView : UserControl, IDisposable
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(180);

    private readonly DispatcherTimer _searchDelayTimer;
    private EventHandler _pendingRootExpansion;
    private IEnumerable<ArchiveFileEntry> _unfilteredItems;
    private List<ArchiveFileEntry> _visibleSearchResults;
    private List<ArchiveFileEntry> _searchMatches;
    private string _completedSearchQuery;
    private SearchViewState _searchState;
    private CancellationTokenSource _searchCancellation;
    private int _activeSearchIndex = -1;
    private long _searchVersion;
    private volatile bool _disposed;

    public event EventHandler<ArchiveFileSelectedEventArgs> FileSelected;

    public ArchiveFileListView()
    {
        _searchDelayTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SearchDelay
        };
        InitializeComponent();

        _searchDelayTimer.Tick += SearchDelayTimer_Tick;
        treeView.SelectedItemChanged += TreeView_SelectedItemChanged;
        treeView.PreviewKeyDown += TreeView_PreviewKeyDown;
        PreviewKeyDown += ArchiveFileListView_PreviewKeyDown;

        SetSearchTexts();
        RefreshSearchActionState();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _disposed = true;
        _searchDelayTimer.Stop();
        _searchDelayTimer.Tick -= SearchDelayTimer_Tick;
        CancelSearch();
        CancelPendingRootExpansion();
        archiveSearchBox.TextChanged -= ArchiveSearchBox_TextChanged;
        treeView.SelectedItemChanged -= TreeView_SelectedItemChanged;
        treeView.PreviewKeyDown -= TreeView_PreviewKeyDown;
        PreviewKeyDown -= ArchiveFileListView_PreviewKeyDown;
        IconManager.ClearCache();
    }

    public void SetArchiveName(string archiveName)
    {
        archiveNameText.Text = archiveName ?? string.Empty;
        archiveNameText.ToolTip = archiveName;
    }

    public void SetDataContext(object context)
    {
        CancelSearch();
        CancelPendingRootExpansion();
        _searchState = null;
        _visibleSearchResults = null;
        _searchMatches = null;
        _completedSearchQuery = null;
        _activeSearchIndex = -1;
        _unfilteredItems = context as IEnumerable<ArchiveFileEntry>;
        treeGrid.DataContext = context;
        ClearSearchStatus();
        RefreshSearchActionState();
        ScheduleRootExpansion();
    }

    private void SetSearchTexts()
    {
        var chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase);

        archiveSearchHint.Text = chinese ? "搜索文件或路径" : "Search files or paths";
        archiveSearchStatus.Text = string.Empty;
        archiveSearchBox.ToolTip = chinese
            ? "按文件名或路径搜索；多个关键词用空格分隔。Esc 清空。"
            : "Search by file name or path. Separate multiple terms with spaces. Press Esc to clear.";
        AutomationProperties.SetName(
            archiveSearchBox,
            chinese ? "搜索压缩包内容" : "Search archive contents");
        archivePreviousButton.ToolTip = chinese
            ? "上一个匹配项（Shift+F3）"
            : "Previous match (Shift+F3)";
        archiveNextButton.ToolTip = chinese
            ? "下一个匹配项（F3）"
            : "Next match (F3)";
        archiveClearButton.ToolTip = chinese
            ? "清空搜索并恢复之前的目录位置"
            : "Clear search and restore the previous tree position";
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

    private void ArchiveSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (archiveSearchHint == null || archiveSearchStatus == null)
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

        _searchState ??= CaptureSearchState();
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
            CancelPendingRootExpansion();
            treeGrid.DataContext = results;
            ScheduleRootExpansion();

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

    private SearchViewState CaptureSearchState()
    {
        return new SearchViewState(
            treeView.SelectedItem as ArchiveFileEntry,
            FindScrollViewer(treeView)?.VerticalOffset ?? 0);
    }

    private void ApplyUnfilteredItems(SearchViewState restoreState)
    {
        if (_unfilteredItems == null)
            return;

        ++_searchVersion;
        CancelPendingRootExpansion();
        treeGrid.DataContext = _unfilteredItems;
        ScheduleRootExpansion();

        if (restoreState?.Selected != null)
            restoreState.Selected.IsSelected = true;

        if (restoreState == null)
            return;

        treeView.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_disposed)
                return;

            treeView.UpdateLayout();
            FindScrollViewer(treeView)?.ScrollToVerticalOffset(restoreState.VerticalOffset);
        }));
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

        archiveSearchBox.Clear();
        FocusSearchBox();
    }

    private static string Translate(string english, string chinese)
    {
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? chinese
            : english;
    }

    private static string FormatMatchCount(int count)
    {
        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return $"{count} 个匹配项";
        }

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
        OpenSearchMatch(0);
    }

    private void ArchivePreviousButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSearchMatch(-1);
    }

    private void ArchiveNextButton_Click(object sender, RoutedEventArgs e)
    {
        MoveSearchMatch(1);
    }

    private void ArchiveClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearSearchAndRestore();
    }

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
            currentIndex = _searchMatches.IndexOf(treeView.SelectedItem as ArchiveFileEntry);

        var nextIndex = ArchiveSearchPresentation.GetNextMatchIndex(
            currentIndex,
            _searchMatches.Count,
            direction);
        OpenSearchMatch(nextIndex);
    }

    private void OpenSearchMatch(int index)
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
        treeView.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_disposed)
                return;

            var container = FindContainer(target);
            if (container == null)
            {
                target.IsSelected = true;
                return;
            }

            container.IsSelected = true;
            container.Focus();
            container.BringIntoView();
        }));
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

    private void ScheduleRootExpansion()
    {
        _pendingRootExpansion = TryExpandSingleRoot;
        treeView.LayoutUpdated += _pendingRootExpansion;
        TryExpandSingleRoot(treeView, EventArgs.Empty);
    }

    private void TryExpandSingleRoot(object sender, EventArgs e)
    {
        // Wait for the ItemsSource binding during the first layout pass.
        if (treeView.Items.Count == 0)
        {
            if (treeView.IsLoaded)
                CancelPendingRootExpansion();

            return;
        }

        // The view is normally populated with one archive root.
        if (treeView.Items.Count > 1)
        {
            CancelPendingRootExpansion();
            return;
        }

        if (treeView.ItemContainerGenerator.ContainerFromIndex(0) is not TreeViewItem root)
            return;

        CancelPendingRootExpansion();
        root.IsExpanded = true;
    }

    private void CancelPendingRootExpansion()
    {
        if (_pendingRootExpansion == null)
            return;

        treeView.LayoutUpdated -= _pendingRootExpansion;
        _pendingRootExpansion = null;
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var entry = e.NewValue as ArchiveFileEntry;
        UpdateSearchSelection(entry);
        FileSelected?.Invoke(this, new ArchiveFileSelectedEventArgs(entry));
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

    private void TreeView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Home or Key.End) || Keyboard.Modifiers != ModifierKeys.None)
            return;

        var direction = e.Key == Key.Home
            ? FocusNavigationDirection.First
            : FocusNavigationDirection.Last;
        if (treeView.MoveFocus(new TraversalRequest(direction)))
            e.Handled = true;
    }

    private sealed record SearchViewState(
        ArchiveFileEntry Selected,
        double VerticalOffset);
}

public sealed class ArchiveFileSelectedEventArgs(ArchiveFileEntry entry) : EventArgs
{
    public ArchiveFileEntry Entry { get; } = entry;
}
