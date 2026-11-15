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
using System.Windows.Threading;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

public partial class ArchiveInfoPanel : UserControl, IDisposable, INotifyPropertyChanged
{
    private const string SettingsDomain = "QuickLook.Plugin.ArchiveViewer";
    private static readonly TimeSpan SelectionPreviewDelay = TimeSpan.FromMilliseconds(50);

    private readonly Dictionary<string, ArchiveFileEntry> _fileEntries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ArchiveEntryPreviewCache _embeddedPreviewCache = new();
    private readonly string _archivePath;
    private readonly string _translationsPath;
    private volatile bool _disposed;
    private CancellationTokenSource _archiveLoadCancellation;
    private double _loadPercent;
    private int _previewVersion;
    private CancellationTokenSource _previewCancellation;
    private IDetachedPreviewSession _detachedPreview;
    private string _temporaryDirectory;
    private readonly DispatcherTimer _selectionPreviewTimer;
    private IEmbeddedPreviewSession _embeddedPreview;
    private CancellationTokenSource _embeddedPreviewCancellation;
    private string _embeddedPreviewTitle;
    private ArchiveFileEntry _pendingSelectionPreview;
    private int _embeddedPreviewVersion;
    private ulong _totalZippedSize;
    private string _type;

    public ArchiveInfoPanel(string path)
    {
        _selectionPreviewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SelectionPreviewDelay
        };

        InitializeComponent();
        _archivePath = path;
        _translationsPath = Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
            "Translations.config");
        _ = Task.Run(ArchiveEntryExtractor.CleanupStalePreviewDirectories);

        // design-time only
        Resources.MergedDictionaries.Clear();

        fileListView.SetArchiveName(Path.GetFileName(path));
        fileListView.PreviewRequested += FileListView_PreviewRequested;
        fileListView.SelectionPreviewRequested += FileListView_SelectionPreviewRequested;
        _selectionPreviewTimer.Tick += SelectionPreviewTimer_Tick;
        lblLoading.Content = Translate("ArchivePreview_LoadingArchive", "Loading archive ...");

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

        var archiveLoad = Interlocked.Exchange(ref _archiveLoadCancellation, null);
        archiveLoad?.Cancel();

        fileListView.PreviewRequested -= FileListView_PreviewRequested;
        fileListView.SelectionPreviewRequested -= FileListView_SelectionPreviewRequested;
        _selectionPreviewTimer.Stop();
        _selectionPreviewTimer.Tick -= SelectionPreviewTimer_Tick;
        StopEmbeddedPreview();
        _embeddedPreviewCache.Dispose();
        if (_detachedPreview != null)
        {
            _detachedPreview.Closed -= DetachedPreview_Closed;
            _detachedPreview.Dispose();
            _detachedPreview = null;
        }
        StopCurrentPreview();
        fileListView.Dispose();
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void FileListView_PreviewRequested(object sender, ArchiveFileSelectedEventArgs e)
    {
        if (_disposed || e.Entry is not { IsFolder: false })
            return;

        _selectionPreviewTimer.Stop();
        _pendingSelectionPreview = null;
        _ = PreviewEntryAsync(e.Entry);
    }

    private void FileListView_SelectionPreviewRequested(
        object sender,
        ArchiveFileSelectedEventArgs e)
    {
        if (_disposed || e.Entry is not { IsFolder: false })
            return;

        _pendingSelectionPreview = e.Entry;
        _selectionPreviewTimer.Stop();
        CancelPendingEmbeddedPreview();
        fileListView.ShowPreviewStatus(
            e.Entry.Name,
            Translate("ArchivePreview_Loading", "Preparing preview ..."),
            showProgress: true);
        _selectionPreviewTimer.Start();
    }

    private void SelectionPreviewTimer_Tick(object sender, EventArgs e)
    {
        _selectionPreviewTimer.Stop();
        var entry = _pendingSelectionPreview;
        _pendingSelectionPreview = null;
        if (entry != null)
            _ = PreviewSelectionAsync(entry);
    }

    private async Task PreviewSelectionAsync(ArchiveFileEntry entry)
    {
        if (entry.Encrypted)
        {
            fileListView.ShowPreviewStatus(
                entry.Name,
                Translate(
                    "ArchivePreview_Encrypted",
                    "Encrypted files cannot be previewed yet."),
                showProgress: false);
            return;
        }

        if (entry.Size > ArchiveEntryExtractor.MaxPreviewBytes)
        {
            fileListView.ShowPreviewStatus(
                entry.Name,
                Translate(
                    "ArchivePreview_TooLarge",
                    "This file is larger than 512 MB and was not extracted."),
                showProgress: false);
            return;
        }

        var version = ++_embeddedPreviewVersion;
        var cancellation = new CancellationTokenSource();
        _embeddedPreviewCancellation = cancellation;
        _embeddedPreviewTitle = entry.Name;
        fileListView.ShowPreviewStatus(
            entry.Name,
            Translate("ArchivePreview_Loading", "Preparing preview ..."),
            showProgress: true);

        ExtractedArchiveEntry extracted = null;
        try
        {
            extracted = await Task.Run(() =>
                GetOrExtractEmbeddedEntry(entry, cancellation.Token));

            if (_disposed || version != _embeddedPreviewVersion ||
                !ReferenceEquals(_embeddedPreviewCancellation, cancellation))
            {
                return;
            }

            var session = PluginHelper.CreateEmbeddedPreview(
                extracted.FilePath,
                fileListView);
            if (session == null)
            {
                fileListView.ShowPreviewStatus(
                    entry.Name,
                    Translate(
                        "ArchivePreview_Failed",
                        "Preview failed. See the diagnostic log for more details."),
                    showProgress: false);
                return;
            }

            await session.StartAsync();

            if (_disposed || version != _embeddedPreviewVersion ||
                !ReferenceEquals(_embeddedPreviewCancellation, cancellation))
            {
                session.Dispose();
                return;
            }

            SwapEmbeddedPreview(session, entry.Name);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ProcessHelper.WriteLog(ex.ToString());
            if (version == _embeddedPreviewVersion && !_disposed)
            {
                fileListView.ShowPreviewStatus(
                    entry.Name,
                    Translate(
                        "ArchivePreview_Failed",
                        "Preview failed. See the diagnostic log for more details."),
                    showProgress: false);
            }
        }
        finally
        {
            if (ReferenceEquals(_embeddedPreviewCancellation, cancellation))
                _embeddedPreviewCancellation = null;

            cancellation.Dispose();
        }
    }

    private void EmbeddedPreview_Changed(object sender, EventArgs e)
    {
        if (!_disposed && ReferenceEquals(sender, _embeddedPreview))
            UpdateEmbeddedPreview();
    }

    private ExtractedArchiveEntry GetOrExtractEmbeddedEntry(
        ArchiveFileEntry entry,
        CancellationToken cancellationToken)
    {
        if (_embeddedPreviewCache.TryGet(
            _archivePath,
            entry.ArchivePath,
            entry.Size,
            out var cached))
        {
            return cached;
        }

        var extracted = ArchiveEntryExtractor.Extract(
            _archivePath,
            entry.ArchivePath,
            entry.Size,
            cancellationToken);

        if (!_embeddedPreviewCache.TryAdd(
            _archivePath,
            entry.ArchivePath,
            entry.Size,
            extracted))
        {
            ArchiveEntryExtractor.DeleteDirectoryWhenUnused(
                extracted.TemporaryDirectory);
        }

        return extracted;
    }

    private void SwapEmbeddedPreview(
        IEmbeddedPreviewSession session,
        string title)
    {
        var previous = _embeddedPreview;
        _embeddedPreview = session;
        _embeddedPreviewTitle = title;
        session.Changed += EmbeddedPreview_Changed;
        UpdateEmbeddedPreview();

        if (previous != null)
        {
            previous.Changed -= EmbeddedPreview_Changed;
            previous.Dispose();
        }
    }

    private void UpdateEmbeddedPreview()
    {
        var session = _embeddedPreview;
        if (session == null || _disposed)
            return;

        if (!string.IsNullOrEmpty(session.Error))
        {
            fileListView.ShowPreviewStatus(
                _embeddedPreviewTitle,
                Translate(
                    "ArchivePreview_Failed",
                    "Preview failed. See the diagnostic log for more details."),
                showProgress: false);
            return;
        }

        if (session.ViewerContent != null)
        {
            fileListView.ShowPreviewContent(
                _embeddedPreviewTitle,
                session.ViewerContent);
            return;
        }

        fileListView.ShowPreviewStatus(
            _embeddedPreviewTitle,
            session.IsBusy
                ? Translate("ArchivePreview_Loading", "Preparing preview ...")
                : Translate(
                    "No preview is available for this file.",
                    "此文件暂无可用的内容预览。"),
            session.IsBusy);
    }

    private void CancelPendingEmbeddedPreview()
    {
        ++_embeddedPreviewVersion;

        var cancellation = _embeddedPreviewCancellation;
        _embeddedPreviewCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void StopEmbeddedPreview()
    {
        CancelPendingEmbeddedPreview();

        var session = _embeddedPreview;
        _embeddedPreview = null;
        if (session != null)
        {
            session.Changed -= EmbeddedPreview_Changed;
            session.Dispose();
        }
    }

    private async Task PreviewEntryAsync(ArchiveFileEntry entry)
    {
        if (entry.Encrypted)
        {
            ShowPreviewMessage(entry.Name, Translate(
                "ArchivePreview_Encrypted",
                "Encrypted files cannot be previewed yet."));
            return;
        }

        if (entry.Size > ArchiveEntryExtractor.MaxPreviewBytes)
        {
            ShowPreviewMessage(entry.Name, Translate(
                "ArchivePreview_TooLarge",
                "This file is larger than 512 MB and was not extracted."));
            return;
        }

        StopCurrentPreview(deleteTemporary: false);
        var version = ++_previewVersion;
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;

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

            var previousTemporaryDirectory = _temporaryDirectory;
            _temporaryDirectory = extracted.TemporaryDirectory;
            var preview = EnsureDetachedPreview();
            if (preview == null)
            {
                _temporaryDirectory = previousTemporaryDirectory;
                ArchiveEntryExtractor.DeleteDirectoryWhenUnused(
                    extracted.TemporaryDirectory);
                ShowPreviewMessage(entry.Name, Translate(
                    "ArchivePreview_Failed",
                    "Preview failed. See the diagnostic log for more details."));
                return;
            }

            try
            {
                await preview.ShowAsync(extracted.FilePath);
                ArchiveEntryExtractor.DeleteDirectoryWhenUnused(
                    previousTemporaryDirectory);
            }
            catch (Exception ex)
            {
                ProcessHelper.WriteLog(ex.ToString());
                if (version == _previewVersion &&
                    ReferenceEquals(_temporaryDirectory, extracted.TemporaryDirectory))
                {
                    _temporaryDirectory = previousTemporaryDirectory;
                }

                ArchiveEntryExtractor.DeleteDirectoryWhenUnused(
                    extracted.TemporaryDirectory);
                if (version == _previewVersion)
                {
                    ShowPreviewMessage(entry.Name, Translate(
                        "ArchivePreview_Failed",
                        "Preview failed. See the diagnostic log for more details."));
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (extracted != null)
                ArchiveEntryExtractor.TryDeleteDirectory(extracted.TemporaryDirectory);
        }
        catch (Exception ex)
        {
            var failure = ArchivePreviewFailureClassifier.Classify(ex);
            if (failure is not (ArchivePreviewFailure.Encrypted or ArchivePreviewFailure.TooLarge))
                ProcessHelper.WriteLog(ex.ToString());

            if (version == _previewVersion)
                ShowPreviewMessage(entry.Name, GetFailureMessage(failure));
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
                _previewCancellation = null;

            cancellation.Dispose();
        }
    }

    private void StopCurrentPreview(bool deleteTemporary = true)
    {
        _previewVersion++;

        var cancellation = _previewCancellation;
        _previewCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();

        if (deleteTemporary)
            DeleteCurrentTemporaryDirectory();
    }

    private void DeleteCurrentTemporaryDirectory()
    {
        var temporaryDirectory = _temporaryDirectory;
        _temporaryDirectory = null;
        ArchiveEntryExtractor.DeleteDirectoryWhenUnused(temporaryDirectory);
    }

    private IDetachedPreviewSession EnsureDetachedPreview()
    {
        if (_detachedPreview != null)
            return _detachedPreview;

        var owner = Window.GetWindow(this);
        if (owner == null)
            return null;

        _detachedPreview = PluginHelper.CreateDetachedPreview(owner, this);
        if (_detachedPreview != null)
            _detachedPreview.Closed += DetachedPreview_Closed;

        return _detachedPreview;
    }

    private void DetachedPreview_Closed(object sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _detachedPreview))
            return;

        _detachedPreview.Closed -= DetachedPreview_Closed;
        _detachedPreview = null;
        DeleteCurrentTemporaryDirectory();
    }

    private void ShowPreviewMessage(string title, string message)
    {
        var owner = Window.GetWindow(this);
        if (owner == null)
        {
            MessageBox.Show(
                message,
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(
            owner,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private string Translate(string id, string fallback)
    {
        return TranslationHelper.Get(id, _translationsPath, failsafe: fallback);
    }

    private void BeginLoadArchive(string path)
    {
        var cancellation = new CancellationTokenSource();
        _archiveLoadCancellation = cancellation;
        _ = Task.Run(() => LoadArchiveInBackground(path, cancellation));
    }

    private void LoadArchiveInBackground(string path, CancellationTokenSource cancellation)
    {
        try
        {
            LoadArchiveCore(path, cancellation.Token);
        }
        finally
        {
            Interlocked.CompareExchange(ref _archiveLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void LoadArchiveCore(string path, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            _totalZippedSize = (ulong)new FileInfo(path).Length;

            var root = new ArchiveFileEntry(Path.GetFileName(path), true);
            _fileEntries.Add(string.Empty, root);

            LoadItemsFromArchive(path, token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            if (token.IsCancellationRequested)
                return;

            ProcessHelper.WriteLog(e.ToString());
            QueueUiAction(() => ShowArchiveLoadFailure(e));
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
            t = $": {d} and {f}";
        else if (string.IsNullOrEmpty(d) && string.IsNullOrEmpty(f))
            t = string.Empty;
        else
            t = $": {d}{f}";

        QueueUiAction(() =>
        {
            if (_disposed || token.IsCancellationRequested)
                return;

            fileListView.SetDataContext(_fileEntries[string.Empty]);
            fileListView.SetArchiveSummary(
                $"{_type} archive{t}",
                $"Compressed size: {((long)_totalZippedSize).ToPrettySize(2)}",
                $"Uncompressed size: {((long)sizeU).ToPrettySize(2)}");
            LoadPercent = 100d;
        });
    }

    private void ReportLoadProgress(ArchiveLoadProgressThrottle throttle, FileStream fileStream)
    {
        var percent = fileStream.Length <= 0
            ? 100d
            : 100d * fileStream.Position / fileStream.Length;

        if (throttle.ShouldReport(percent, Environment.TickCount64))
            QueueUiAction(() =>
            {
                if (!_disposed)
                    LoadPercent = percent;
            });
    }

    private void QueueUiAction(Action action)
    {
        if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        try
        {
            Dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The preview can be closed while its background scan is finishing.
        }
    }

    private void LoadItemsFromArchive(string path, CancellationToken cancellationToken)
    {
        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var progress = new ArchiveLoadProgressThrottle();

        // ReaderFactory is slow... so limit its usage
        string[] useReader = [".tar.gz", ".tgz", ".tar.bz2", ".tar.lz", ".tar.xz"];

        if (useReader.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
        {
            var reader = ReaderFactory.Open(fileStream, new ChardetReaderOptions());

            _type = reader.ArchiveType.ToString();

            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportLoadProgress(progress, fileStream);
                ProcessByLevel(reader.Entry);
            }
        }
        else
        {
            var archive = ArchiveFactory.Open(fileStream, new ChardetReaderOptions());

            _type = archive.Type.ToString();

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReportLoadProgress(progress, fileStream);
                ProcessByLevel(entry);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ProcessByLevel(IEntry entry)
    {
        var pf = GetPathFragments(ArchiveEntryExtractor.NormalizeArchivePath(entry.Key));
        if (pf.Length == 0)
            return;

        // process folders. When entry is a directory, all fragments are folders.
        pf.Take(entry.IsDirectory ? pf.Length : pf.Length - 1)
            .ForEach(f =>
            {
                // skip if current dir is already added
                if (_fileEntries.ContainsKey(f))
                    return;

                _fileEntries.TryGetValue(GetDirectoryName(f), out var parent);

                var afe = new ArchiveFileEntry(Path.GetFileName(f), true, parent, f);

                _fileEntries.TryAdd(f, afe);
            });

        // add the last path fragments, which is a file
        if (!entry.IsDirectory)
        {
            var file = pf[^1];

            // ZIP and TAR archives can legally contain duplicate paths. The tree
            // has one node per path, so keep the first item and continue listing
            // the rest instead of failing the complete archive.
            if (_fileEntries.ContainsKey(file))
                return;

            _fileEntries.TryGetValue(GetDirectoryName(file), out var parent);

            _fileEntries.Add(file, new ArchiveFileEntry(Path.GetFileName(file), false, parent, file)
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

    private void ShowArchiveLoadFailure(Exception exception)
    {
        if (_disposed)
            return;

        loadingProgress.Visibility = Visibility.Collapsed;
        lblLoading.Content = GetFailureMessage(ArchivePreviewFailureClassifier.Classify(exception));
    }

    private string GetFailureMessage(ArchivePreviewFailure failure)
    {
        return failure switch
        {
            ArchivePreviewFailure.Encrypted => Translate(
                "ArchivePreview_Encrypted",
                "Encrypted files cannot be previewed yet."),
            ArchivePreviewFailure.TooLarge => Translate(
                "ArchivePreview_TooLarge",
                "This file is larger than 512 MB and was not extracted."),
            ArchivePreviewFailure.MissingEntry => Translate(
                "ArchivePreview_EntryMissing",
                "The selected file is no longer present in the archive."),
            ArchivePreviewFailure.Incomplete => Translate(
                "ArchivePreview_Incomplete",
                "The archive is incomplete or one of its volumes is missing."),
            ArchivePreviewFailure.InvalidFormat => Translate(
                "ArchivePreview_Invalid",
                "The archive is damaged or uses an unsupported format."),
            _ => Translate(
                "ArchivePreview_Failed",
                "Preview failed. See the diagnostic log for more details."),
        };
    }

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
