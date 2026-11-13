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
using System.Windows.Controls.Primitives;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

public partial class ArchiveInfoPanel : UserControl, IDisposable, INotifyPropertyChanged
{
    private const string SplitRatioSetting = "ArchiveFileListRatio";
    private const string SettingsDomain = "QuickLook.Plugin.ArchiveViewer";

    private readonly Dictionary<string, ArchiveFileEntry> _fileEntries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string _archivePath;
    private readonly string _translationsPath;
    private volatile bool _disposed;
    private CancellationTokenSource _archiveLoadCancellation;
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

        fileListView.SetArchiveName(Path.GetFileName(path));
        fileListView.FileSelected += FileListView_FileSelected;
        lblLoading.Content = Translate("ArchivePreview_LoadingArchive", "Loading archive ...");
        SetPreviewMessage(Translate("ArchivePreview_SelectFile", "Select a file in the archive to preview it."));
        Loaded += ArchiveInfoPanel_Loaded;

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

        Loaded -= ArchiveInfoPanel_Loaded;
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

    private void ArchiveInfoPanel_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySplitRatio(SettingHelper.Get(
            SplitRatioSetting,
            ArchiveSplitLayout.DefaultRatio,
            SettingsDomain));
    }

    private void ArchiveSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var ratio = ArchiveSplitLayout.RatioFromWidths(
            fileListColumn.ActualWidth,
            previewColumn.ActualWidth);
        ApplySplitRatio(ratio);
        SettingHelper.Set(SplitRatioSetting, ratio, SettingsDomain);
    }

    private void ApplySplitRatio(double ratio)
    {
        var normalized = ArchiveSplitLayout.Normalize(ratio);
        fileListColumn.Width = new GridLength(normalized, GridUnitType.Star);
        previewColumn.Width = new GridLength(1 - normalized, GridUnitType.Star);
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
        catch (Exception ex)
        {
            var failure = ArchivePreviewFailureClassifier.Classify(ex);
            if (failure is not (ArchivePreviewFailure.Encrypted or ArchivePreviewFailure.TooLarge))
                ProcessHelper.WriteLog(ex.ToString());

            if (version == _previewVersion)
                SetPreviewFailure(failure);
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
            t = $", {d} and {f}";
        else if (string.IsNullOrEmpty(d) && string.IsNullOrEmpty(f))
            t = string.Empty;
        else
            t = $", {d}{f}";

        QueueUiAction(() =>
        {
            if (_disposed || token.IsCancellationRequested)
                return;

            fileListView.SetDataContext(_fileEntries[string.Empty].Children.Keys);
            archiveCount.Content =
                $"{_type} archive{t}";
            archiveSizeC.Content =
                $"Compressed size {((long)_totalZippedSize).ToPrettySize(2)}";
            archiveSizeU.Content = $"Uncompressed size {((long)sizeU).ToPrettySize(2)}";
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

    private void SetPreviewFailure(ArchivePreviewFailure failure)
    {
        SetPreviewMessage(GetFailureMessage(failure));
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
