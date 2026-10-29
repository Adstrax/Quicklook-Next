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
using System;
using System.Windows;
using System.Windows.Controls;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

public partial class ArchiveFileListView : UserControl, IDisposable
{
    public event EventHandler<ArchiveFileSelectedEventArgs> FileSelected;

    public ArchiveFileListView()
    {
        InitializeComponent();
        treeView.SelectedItemChanged += TreeView_SelectedItemChanged;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        IconManager.ClearCache();
    }

    public void SetDataContext(object context)
    {
        treeGrid.DataContext = context;

        treeView.LayoutUpdated += (sender, e) =>
        {
            // return when empty
            if (treeView.Items.Count == 0)
                return;

            // return when there are more than one root nodes
            if (treeView.Items.Count > 1)
                return;

            var root = (TreeViewItem)treeView.ItemContainerGenerator.ContainerFromItem(treeView.Items[0]);
            if (root == null)
                return;

            root.IsExpanded = true;
        };
    }

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        FileSelected?.Invoke(this, new ArchiveFileSelectedEventArgs(e.NewValue as ArchiveFileEntry));
    }
}

public sealed class ArchiveFileSelectedEventArgs(ArchiveFileEntry entry) : EventArgs
{
    public ArchiveFileEntry Entry { get; } = entry;
}
