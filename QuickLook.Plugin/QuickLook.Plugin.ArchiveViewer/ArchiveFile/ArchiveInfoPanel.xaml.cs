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

using PureSharpCompress.Archives;
using PureSharpCompress.Common;
using PureSharpCompress.Readers;
using QuickLook.Common.Annotations;
using QuickLook.Common.ExtensionMethods;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

public partial class ArchiveInfoPanel : UserControl, IDisposable, INotifyPropertyChanged
{
    private readonly Dictionary<string, ArchiveFileEntry> _fileEntries = [];
    private readonly string _archivePath;
    private readonly string _translationsPath;
    private bool _disposed;
    private double _loadPercent;
    private int _previewVersion;
    private CancellationTokenSource _previewCancellation;
    private IEmbeddedPreviewSession _previewSession;
    private string _temporaryDirectory;
    private ulong _totalZippedSize;
    private string _type;

    public ArchiveInfoPanel(string path)
    {
        InitializeComponent();
        _archivePath = path;
        _translationsPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
            "Translations.config");
        _ = Task.Run(ArchiveEntryExtractor.CleanupStalePreviewDirectories);

        // design-time only
        Resources.MergedDictionaries.Clear();

        fileListView.FileSelected += FileListView_FileSelected;
        SetPreviewMessage(Translate("ArchivePreview_SelectFile", "Select a file in the archive to preview it."));

        BeginLoadArchive(path);
    }

    public double LoadPercent
    {
        get => _loadPercent;
        private set
        {
            if (value == _loadPercent) return;
            _loadPercent = value;
            OnPropertyChanged();
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _disposed = true;

        fileListView.FileSelected -= FileListView_FileSelected;
        StopCurrentPreview();
        fileListView.Dispose();
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private async void FileListView_FileSelected(object sender, ArchiveFileSelectedEventArgs e)
    {
        var entry = e.Entry;
        if (_disposed)
            return;

        if (entry == null || entry.IsFolder)
        {
            StopCurrentPreview();
            previewTitle.Text = entry?.Name ?? string.Empty;
            SetPreviewMessage(Translate(
                "ArchivePreview_SelectFile",
                "Select a file in the archive to preview it."));
            return;
        }

        await PreviewEntryAsync(entry);
    }

    private async Task PreviewEntryAsync(ArchiveFileEntry entry)
    {
        StopCurrentPreview();
        var version = _previewVersion;

        previewTitle.Text = entry.Name;

        if (entry.Encrypted)
        {
            SetPreviewMessage(Translate(
                "ArchivePreview_Encrypted",
                "Encrypted files cannot be previewed yet."));
            return;
        }

        if (entry.Size > ArchiveEntryExtractor.MaxPreviewBytes)
        {
            SetPreviewMessage(Translate(
                "ArchivePreview_TooLarge",
                "This file is larger than 512 MB and was not extracted."));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        SetPreviewMessage(
            Translate("ArchivePreview_Loading", "Preparing preview ..."),
            showProgress: true);

        ExtractedArchiveEntry extracted = null;
        try
        {
            extracted = await Task.Run(() => ArchiveEntryExtractor.Extract(
                _archivePath,
                entry.ArchivePath,
                entry.Size,
                cancellation.Token));

            if (_disposed || version != _previewVersion ||
                !ReferenceEquals(_previewCancellation, cancellation))
            {
                ArchiveEntryExtractor.TryDeleteDirectory(extracted.TemporaryDirectory);
                return;
            }

            _temporaryDirectory = extracted.TemporaryDirectory;

            var session = PluginHelper.CreateEmbeddedPreview(extracted.FilePath, this);
            if (session == null)
            {
                SetPreviewMessage(Translate(
                    "ArchivePreview_Unavailable",
                    "No preview is available for this file."));
                return;
            }

            _previewSession = session;
            session.Changed += PreviewSession_Changed;
            await session.StartAsync();

            if (!_disposed && ReferenceEquals(_previewSession, session))
                UpdatePreviewFromSession();
        }
        catch (OperationCanceledException)
        {
            if (extracted != null)
                ArchiveEntryExtractor.TryDeleteDirectory(extracted.TemporaryDirectory);
        }
        catch (ArchiveEntryTooLargeException)
        {
            if (version == _previewVersion)
            {
                SetPreviewMessage(Translate(
                    "ArchivePreview_TooLarge",
                    "This file is larger than 512 MB and was not extracted."));
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (version == _previewVersion)
            {
                SetPreviewMessage(Translate(
                    "ArchivePreview_Encrypted",
                    "Encrypted files cannot be previewed yet."));
            }
        }
        catch (Exception ex)
        {
            ProcessHelper.WriteLog(ex.ToString());
            if (version == _previewVersion)
            {
                SetPreviewMessage(Translate(
                    "ArchivePreview_Failed",
                    "Preview failed. See the diagnostic log for more details."));
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
                _previewCancellation = null;

            cancellation.Dispose();
        }
    }

    private void PreviewSession_Changed(object sender, EventArgs e)
    {
        if (!_disposed && ReferenceEquals(sender, _previewSession))
            UpdatePreviewFromSession();
    }

    private void UpdatePreviewFromSession()
    {
        var session = _previewSession;
        if (session == null)
            return;

        if (!string.IsNullOrEmpty(session.Error))
        {
            SetPreviewMessage(Translate(
                "ArchivePreview_Failed",
                "Preview failed. See the diagnostic log for more details."));
            return;
        }

        if (session.ViewerContent != null)
        {
            previewContent.Content = session.ViewerContent;
            return;
        }

        if (session.IsBusy)
            SetPreviewMessage(
                Translate("ArchivePreview_Loading", "Preparing preview ..."),
                showProgress: true);
        else
            SetPreviewMessage(Translate(
                "ArchivePreview_Unavailable",
                "No preview is available for this file."));
    }

    private void StopCurrentPreview()
    {
        _previewVersion++;

        var cancellation = _previewCancellation;
        _previewCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();

        var session = _previewSession;
        _previewSession = null;
        if (session != null)
        {
            session.Changed -= PreviewSession_Changed;
            session.Dispose();
        }

        previewContent.Content = null;

        var temporaryDirectory = _temporaryDirectory;
        _temporaryDirectory = null;
        ArchiveEntryExtractor.DeleteDirectoryWhenUnused(temporaryDirectory);
    }

    private void SetPreviewMessage(string message, bool showProgress = false)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 320,
        };

        if (showProgress)
        {
            panel.Children.Add(new ProgressBar
            {
                Width = 130,
                Height = 4,
                Margin = new Thickness(0, 0, 0, 14),
                IsIndeterminate = true,
            });
        }

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 13,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "WindowTextForegroundAlternative");
        panel.Children.Add(text);

        previewContent.Content = panel;
    }

    private string Translate(string id, string fallback)
    {
        return TranslationHelper.Get(id, _translationsPath, failsafe: fallback);
    }

    private void BeginLoadArchive(string path)
    {
        new Task(() =>
        {
            _totalZippedSize = (ulong)new FileInfo(path).Length;

            var root = new ArchiveFileEntry(Path.GetFileName(path), true);
            _fileEntries.Add(string.Empty, root);

            try
            {
                LoadItemsFromArchive(path);
            }
            catch (Exception e)
            {
                ProcessHelper.WriteLog(e.ToString());
                Dispatcher.Invoke(() => { lblLoading.Content = "Preview failed. See log for more details."; });
                return;
            }

            var folders = -1; // do not count root node
            var files = 0;
            ulong sizeU = 0L;
            _fileEntries.ForEach(e =>
            {
                if (e.Value.IsFolder)
                    folders++;
                else
                    files++;

                sizeU += e.Value.Size;
            });

            string t;
            var d = folders != 0 ? $"{folders} folders" : string.Empty;
            var f = files != 0 ? $"{files} files" : string.Empty;
            if (!string.IsNullOrEmpty(d) && !string.IsNullOrEmpty(f))
                t = $", {d} and {f}";
            else if (string.IsNullOrEmpty(d) && string.IsNullOrEmpty(f))
                t = string.Empty;
            else
                t = $", {d}{f}";

            Dispatcher.Invoke(() =>
            {
                if (_disposed)
                    return;

                fileListView.SetDataContext(_fileEntries[string.Empty].Children.Keys);
                archiveCount.Content =
                    $"{_type} archive{t}";
                archiveSizeC.Content =
                    $"Compressed size {((long)_totalZippedSize).ToPrettySize(2)}";
                archiveSizeU.Content = $"Uncompressed size {((long)sizeU).ToPrettySize(2)}";
            });

            LoadPercent = 100d;
        }).Start();
    }

    private void LoadItemsFromArchive(string path)
    {
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // ReaderFactory is slow... so limit its usage
        string[] useReader = [".tar.gz", ".tgz", ".tar.bz2", ".tar.lz", ".tar.xz"];

        if (useReader.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            var reader = ReaderFactory.Open(fileStream, new ChardetReaderOptions());

            _type = reader.ArchiveType.ToString();

            while (reader.MoveToNextEntry())
            {
                if (_disposed)
                    return;
                LoadPercent = 100d * fileStream.Position / fileStream.Length;
                ProcessByLevel(reader.Entry);
            }
        }
        else
        {
            var archive = ArchiveFactory.Open(fileStream, new ChardetReaderOptions());

            _type = archive.Type.ToString();

            foreach (var entry in archive.Entries)
            {
                if (_disposed)
                    return;
                LoadPercent = 100d * fileStream.Position / fileStream.Length;
                ProcessByLevel(entry);
            }
        }
    }

    private void ProcessByLevel(IEntry entry)
    {
        var pf = GetPathFragments(entry.Key);

        // process folders. When entry is a directory, all fragments are folders.
        pf.Take(entry.IsDirectory ? pf.Length : pf.Length - 1)
            .ForEach(f =>
            {
                // skip if current dir is already added
                if (_fileEntries.ContainsKey(f))
                    return;

                _fileEntries.TryGetValue(GetDirectoryName(f), out var parent);

                var afe = new ArchiveFileEntry(Path.GetFileName(f), true, parent, f);

                _fileEntries.Add(f, afe);
            });

        // add the last path fragments, which is a file
        if (!entry.IsDirectory)
        {
            var file = pf.Last();

            _fileEntries.TryGetValue(GetDirectoryName(file), out var parent);

            _fileEntries.Add(file, new ArchiveFileEntry(Path.GetFileName(entry.Key), false, parent, file)
            {
                Encrypted = entry.IsEncrypted,
                Size = (ulong)entry.Size,
                ModifiedDate = entry.LastModifiedTime ?? new DateTime()
            });
        }
    }

    private string GetDirectoryName(string path)
    {
        var d = Path.GetDirectoryName(path);

        return d ?? string.Empty;
    }

    private string[] GetPathFragments(string path)
    {
        if (string.IsNullOrEmpty(path))
            return [];

        var frags = path.Split('\\', '/').Where(f => !string.IsNullOrEmpty(f)).ToArray();

        return [.. frags.Select((s, i) => frags.Take(i + 1).Aggregate((a, b) => a + "\\" + b))];
    }

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
