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

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

public class ArchiveFileEntry : IComparable<ArchiveFileEntry>, INotifyPropertyChanged
{
    private readonly ArchiveFileEntry _parent;
    private int _cachedDepth = -1;
    private int _cachedLevel = -1;
    private bool _isExpanded;
    private bool _isSelected;

    public ArchiveFileEntry(
        string name,
        bool isFolder,
        ArchiveFileEntry parent = null,
        string archivePath = null)
    {
        Name = name;
        IsFolder = isFolder;
        ArchivePath = archivePath ?? string.Empty;

        _parent = parent;
        _parent?.Children.Add(this, false);
    }

    public SortedList<ArchiveFileEntry, bool> Children { get; set; } = [];

    public string Name { get; set; }
    public string ArchivePath { get; set; }
    public bool Encrypted { get; set; }
    public bool IsFolder { get; set; }
    public ulong Size { get; set; }
    public DateTime ModifiedDate { get; set; }
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
    public bool IsSearchMatch { get; set; }
    public string SearchQuery { get; set; }
    public ArchiveFileEntry Parent => _parent;
    public IEnumerable<ArchiveFileEntry> Folders =>
        Children.Keys.Where(entry => entry.IsFolder);
    public string DisplayPath => string.IsNullOrWhiteSpace(ArchivePath)
        ? Name ?? string.Empty
        : ArchivePath.Replace('/', '\\');

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// Returns the maximum depth of all siblings
    /// </summary>
    public int Level
    {
        get
        {
            if (_cachedLevel != -1)
                return _cachedLevel;

            if (_parent == null)
                _cachedLevel = GetDepth();
            else
                _cachedLevel = _parent.Level - 1;

            return _cachedLevel;
        }
    }

    public int CompareTo(ArchiveFileEntry other)
    {
        if (IsFolder == other.IsFolder)
            return string.Compare(Name, other.Name, StringComparison.CurrentCulture);

        if (IsFolder)
            return -1;

        return 1;
    }

    /// <summary>
    /// Returns the number of nodes in the longest path to a leaf
    /// </summary>
    private int GetDepth()
    {
        if (_cachedDepth != -1)
            return _cachedDepth;

        var max = Children.Keys.Count == 0 ? 0 : Children.Keys.Max(r => r.GetDepth());
        _cachedDepth = max + 1;
        return _cachedDepth;
    }

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public override string ToString()
    {
        if (IsFolder)
            return $"{Name}";

        var en = Encrypted ? "*" : string.Empty;
        return $"{Name}{en},{IsFolder},{Size},{ModifiedDate}";
    }
}
