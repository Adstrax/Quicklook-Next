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

using QuickLook.Common.Annotations;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.VideoViewer.AudioTrack;
using QuickLook.Plugin.VideoViewer.LyricTrack;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UtfUnknown;
using WPFMediaKit.DirectShow.Controls;
using WPFMediaKit.DirectShow.MediaPlayers;

namespace QuickLook.Plugin.VideoViewer;

public partial class ViewerPanel : UserControl, IDisposable, INotifyPropertyChanged
{
    private readonly ContextObject _context;
    private BitmapSource _coverArt;
    private DispatcherTimer _lyricTimer;
    private LrcLine[] _lyricLines;
    private MidiPlayer _midiPlayer;
    private CancellationTokenSource _metaCancellation;

    private bool _hasVideo;
    private bool _isPlaying;
    private bool _wasPlaying;
    private bool _shouldLoop;
    private bool _useHardwareAcceleration;
    private bool _disposed;
    private bool _quiesced;
    private bool _mediaSettled;
    private bool _mediaFailed;
    private string _path;
    private string _pendingMetaPath;
    private MediaInfoSnapshot _pendingMetaInfo;
    private int _metaVersion;
    private Stopwatch _loadClock;
    private string _timingPath;
    private readonly VideoRevealGate _revealGate = new();
    private DispatcherTimer _revealFallbackTimer;
    private EventHandler _revealCompositionHandler;
    private bool _published;
    private bool _thumbnailUnavailable;

    public ViewerPanel(ContextObject context)
    {
        InitializeComponent();
        LoadAndInsertGlassLayer();

        // apply global theme
        Resources.MergedDictionaries[0].MergedDictionaries.Clear();

        _context = context;

        mediaElement.MediaUriPlayer.LAVFilterDirectory =
            IntPtr.Size == 8 ? @"LAVFilters-x64\" : @"LAVFilters-x86\";

        //ShowViedoControlContainer(null, null);
        viewerPanel.PreviewMouseMove += ShowViedoControlContainer;

        mediaElement.MediaUriPlayer.PlayerStateChanged += PlayerStateChanged;
        mediaElement.MediaUriPlayer.NewAllocatorFrame += PlayerFramePresented;
        mediaElement.MediaOpened += MediaOpened;
        mediaElement.MediaEnded += MediaEnded;
        mediaElement.MediaFailed += MediaFailed;

        ShouldLoop = SettingHelper.Get("ShouldLoop", false, "QuickLook.Plugin.VideoViewer");
        UseHardwareAcceleration = SettingHelper.Get("UseHardwareAcceleration", false, "QuickLook.Plugin.VideoViewer");

        // Apply persisted HW/SW mode to the underlying player if supported.
        HardwareAccelerationModeChanged(UseHardwareAcceleration);

        string translationFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Translations.config");
        buttonPlayPause.ToolTip = TranslationHelper.Get("BTN_PlayPause", translationFile, failsafe: "Play/Pause");
        buttonLoop.ToolTip = TranslationHelper.Get("BTN_Loop", translationFile, failsafe: "Loop");
        buttonHardwareAcceleration.ToolTip = TranslationHelper.Get("BTN_HardwareAcceleration", translationFile, failsafe: "Hardware/Software Decoding");
        buttonMute.ToolTip = TranslationHelper.Get("BTN_Volume", translationFile, failsafe: "Volume");
        buttonTime.ToolTip = TranslationHelper.Get("BTN_Time", translationFile, failsafe: "Time Elapsed/Remaining");

        buttonPlayPause.Click += TogglePlayPause;
        buttonLoop.Click += ToggleShouldLoop;
        buttonHardwareAcceleration.Click += ToggleHardwareAcceleration;
        buttonTime.Click += (_, _) => buttonTime.Tag = (string)buttonTime.Tag == "Time" ? "Length" : "Time";
        buttonMute.Click += (_, _) => volumeSliderLayer.Visibility = Visibility.Visible;
        volumeSliderLayer.MouseDown += (_, _) => volumeSliderLayer.Visibility = Visibility.Collapsed;

        sliderProgress.PreviewMouseDown += (_, e) =>
        {
            _wasPlaying = mediaElement.IsPlaying;
            mediaElement.Pause();
        };
        sliderProgress.PreviewMouseUp += (_, _) =>
        {
            if (_wasPlaying) mediaElement.Play();
        };

        PreviewMouseWheel += (_, e) => ChangeVolume(e.Delta / 120d * 0.04d);
    }

    private partial void LoadAndInsertGlassLayer();

    public bool HasVideo
    {
        get => _hasVideo;
        private set
        {
            if (value == _hasVideo) return;
            _hasVideo = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (value == _isPlaying) return;
            _isPlaying = value;
            OnPropertyChanged();
        }
    }

    public bool ShouldLoop
    {
        get => _shouldLoop;
        private set
        {
            if (value == _shouldLoop) return;
            _shouldLoop = value;
            OnPropertyChanged();
        }
    }

    public bool UseHardwareAcceleration
    {
        get => _useHardwareAcceleration;
        private set
        {
            if (value == _useHardwareAcceleration) return;
            _useHardwareAcceleration = value;
            OnPropertyChanged();
        }
    }

    public BitmapSource CoverArt
    {
        get => _coverArt;
        private set
        {
            if (ReferenceEquals(value, _coverArt)) return;
            _coverArt = value;
            OnPropertyChanged();
        }
    }

    public void Dispose()
    {
        _quiesced = true;
        _disposed = true;
        Interlocked.Increment(ref _metaVersion);
        CancelPendingMeta();

        CancelReveal();
        _revealGate.TryTakeReveal();
        videoThumbnail.Source = null;
        videoThumbnail.Visibility = Visibility.Collapsed;

        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        SettingHelper.Set("VolumeDouble", LinearVolume, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("ShouldLoop", ShouldLoop, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("UseHardwareAcceleration", UseHardwareAcceleration, "QuickLook.Plugin.VideoViewer");

        try
        {
            if (mediaElement?.MediaUriPlayer != null)
                mediaElement.MediaUriPlayer.NewAllocatorFrame -= PlayerFramePresented;

            mediaElement?.Close();

            Task.Run(() =>
            {
                mediaElement?.MediaUriPlayer.Dispose();
                mediaElement = null;
            });
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }

        StopLyrics();
        _midiPlayer?.Dispose();
        _midiPlayer = null;
    }

    /// <summary>
    /// v6.0.0: stop playback as soon as a different file is selected. Pausing retains the last
    /// frame for the host's stale-content bridge; Dispose closes and releases the graph later.
    /// </summary>
    public void Quiesce()
    {
        if (_quiesced || _disposed)
            return;

        _quiesced = true;
        Interlocked.Increment(ref _metaVersion);
        CancelPendingMeta();
        CancelReveal();
        StopLyrics();
        _pendingMetaPath = null;
        _pendingMetaInfo = null;

        _midiPlayer?.Dispose();
        _midiPlayer = null;

        try
        {
            mediaElement?.Pause();
            IsPlaying = false;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var wnd = Window.GetWindow(this);
            // Do not allow dragging when window is borderless (e.g. fullscreen)
            if (wnd?.WindowStyle == WindowStyle.None)
                return;

            wnd?.DragMove();
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void MediaOpened(object o, RoutedEventArgs args)
    {
        if (mediaElement == null)
            return;

        VideoTiming.Write("media-opened", _timingPath, _loadClock?.ElapsedMilliseconds ?? 0);

        _mediaSettled = true;
        if (_quiesced || _disposed)
        {
            try
            {
                mediaElement.Pause();
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }

            return;
        }

        // MediaOpened means the graph can play; the renderer may still have an
        // empty surface. Reveal the element underneath the thumbnail, but keep
        // the thumbnail on top until the renderer reports a real frame. The old
        // fixed 250 ms grace period could expire before that frame and produce
        // the thumbnail -> black -> video flash. A thumbnail task that finishes
        // even later is suppressed by the same gate in LoadThumbnailAsync.
        mediaElement.Visibility = Visibility.Visible;

        var revealNow = _revealGate.NotifyMediaOpened(mediaElement.HasVideo, Environment.TickCount64);

        if (revealNow)
        {
            // Audio-only media never reports a video frame.
            PublishPanel();
            BeginThumbnailReveal();
        }
        else if (_published)
        {
            StartRevealFallback();
        }
        else if (_thumbnailUnavailable)
        {
            // There is no picture to bridge the gap, so give the renderer a
            // chance to report a frame before attaching a bare black panel.
            StartPublishDeadline();
        }
        // Otherwise the shell thumbnail is still being extracted. Keep the
        // previous preview visible until it arrives; the thumbnail callback
        // publishes this panel atomically with the picture.

        HasVideo = mediaElement.HasVideo;

        // The first placement may have used the extension-only guess. Now
        // that the graph is open, the natural video size is also available as
        // a fallback when MediaInfo could not read the container header.
        if (HasVideo)
        {
            if (_pendingMetaInfo is { Width: > 0, Height: > 0 })
                FitWindowToInfo(_pendingMetaInfo);
            else
                FitWindowToPlayer();
        }
        else
        {
            FitAudioWindow();
        }

        // v1.2.13: tags/cover art are filled in after the frame is showing so
        // they never delay the first frame (spinner).
        if (_pendingMetaPath != null)
        {
            UpdateMeta(_pendingMetaPath, _pendingMetaInfo);
            _pendingMetaPath = null;
            _pendingMetaInfo = null;
        }
    }

    private void MediaFailed(object sender, MediaFailedEventArgs e)
    {
        _mediaSettled = true;
        _mediaFailed = true;
        _revealGate.TryTakeReveal();

        // v5.0.9: WPFMediaKit raises this on its own worker thread. The two lines that clear the
        // thumbnail used to run right here, and touching the visual tree from that thread throws -
        // on a thread nobody catches, which took the whole process down. Every file whose media
        // player cannot open (a 0-byte or truncated video, a container with a missing codec)
        // crashed the app because of it, instead of showing an error. All UI work now happens on
        // the dispatcher, the user gets a sentence instead of a stack trace, and the details go to
        // the log.
        var dispatcher = (sender as MediaUriElement)?.Dispatcher ?? Dispatcher;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed || _quiesced)
                return;

            CancelReveal();
            videoThumbnail.Source = null;
            videoThumbnail.Visibility = Visibility.Collapsed;

            ProcessHelper.WriteLog($"Video preview failed for \"{mediaElement?.Source}\": {e.Exception}");

            var translationFile = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Translations.config");

            // Drop the never-published panel so the error card is not
            // replaced by it when the busy state clears.
            _context.PendingViewerContent = null;
            _context.ViewerContent = new TextBlock()
            {
                Text = TranslationHelper.Get("VV_PlaybackFailed", translationFile,
                    failsafe: "This video could not be played."),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _context.IsBusy = false;
        }));
    }

    private void MediaEnded(object sender, RoutedEventArgs e)
    {
        if (mediaElement == null || _disposed || _quiesced)
            return;

        mediaElement.MediaPosition = 0L;
        if (ShouldLoop)
        {
            IsPlaying = true;

            mediaElement.Play();
        }
        else
        {
            IsPlaying = false;

            mediaElement.Pause();
        }
    }

    private void ShowViedoControlContainer(object sender, MouseEventArgs e)
    {
        var show = (Storyboard)videoControlContainer.FindResource("ShowControlStoryboard");
        if (videoControlContainer.Opacity == 0d || videoControlContainer.Opacity == 1d)
            show.Begin();
    }

    private void AutoHideViedoControlContainer(object sender, EventArgs e)
    {
        if (!HasVideo)
            return;

        if (videoControlContainer.IsMouseOver)
            return;

        var hide = (Storyboard)videoControlContainer.FindResource("HideControlStoryboard");

        hide.Begin();
    }

    private void PlayerStateChanged(PlayerState oldState, PlayerState newState)
    {
        switch (newState)
        {
            case PlayerState.Playing:
                IsPlaying = true;
                break;

            case PlayerState.Paused:
            case PlayerState.Stopped:
            case PlayerState.Closed:
                IsPlaying = false;
                break;
        }
    }

    private void UpdateMeta(string path, MediaInfoSnapshot info)
    {
        if (HasVideo)
            return;

        var version = Interlocked.Increment(ref _metaVersion);
        CancelPendingMeta();
        StopLyrics();

        try
        {
            metaTitle.Text = !string.IsNullOrWhiteSpace(info?.Title)
                ? info.Title
                : Path.GetFileName(path);
            metaArtists.Text = info?.Artist;
            metaAlbum.Text = info?.Album;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            metaTitle.Text = Path.GetFileName(path);
            metaArtists.Text = metaAlbum.Text = string.Empty;
        }

        UpdateMetaVisibility();

        var cancellation = new CancellationTokenSource();
        _metaCancellation = cancellation;
        _ = LoadAudioArtifactsAsync(path, info, version, cancellation);
    }

    private void UpdateMetaVisibility()
    {
        metaArtists.Visibility = string.IsNullOrEmpty(metaArtists.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        metaAlbum.Visibility = string.IsNullOrEmpty(metaAlbum.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async Task LoadAudioArtifactsAsync(
        string path,
        MediaInfoSnapshot info,
        int version,
        CancellationTokenSource cancellation)
    {
        try
        {
            var artifacts = await Task.Run(
                () => BuildAudioArtifacts(path, info, cancellation.Token),
                cancellation.Token).ConfigureAwait(false);

            if (cancellation.IsCancellationRequested ||
                version != Volatile.Read(ref _metaVersion) ||
                _disposed ||
                _quiesced)
                return;

            _ = Dispatcher.BeginInvoke(
                new Action(() => ApplyAudioArtifacts(path, artifacts, version)),
                DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer metadata update or Close superseded this one.
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
        finally
        {
            Interlocked.CompareExchange(ref _metaCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private static AudioMetaArtifacts BuildAudioArtifacts(
        string path,
        MediaInfoSnapshot info,
        CancellationToken cancellationToken)
    {
        BitmapSource cover = null;
        LrcLine[] lyrics = null;
        TagLib.File tagFile = null;
        var lyricPath = Path.ChangeExtension(path, ".lrc");
        var hasSidecarLyrics = File.Exists(lyricPath);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coverBytes = CoverDataExtractor.Extract(info?.CoverData);
            cover = DecodeCover(coverBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // MediaInfo is the fastest source, but it does not report every
            // cover or lyrics tag. TagLib is only opened when it can add
            // something, and both parsers stay on this background thread.
            if (cover is null || !hasSidecarLyrics)
            {
                tagFile = TagLib.File.Create(path);

                if (cover is null)
                    cover = DecodeCover(FindEmbeddedPicture(tagFile));

                if (!hasSidecarLyrics)
                {
                    var embeddedLyrics = FindEmbeddedLyrics(tagFile);
                    if (!string.IsNullOrWhiteSpace(embeddedLyrics))
                        lyrics = [.. LrcHelper.ParseText(embeddedLyrics)];
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
        finally
        {
            tagFile?.Dispose();
        }

        if (hasSidecarLyrics)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var buffer = File.ReadAllBytes(lyricPath);
                var encoding = CharsetDetector.DetectFromBytes(buffer).Detected?.Encoding ?? Encoding.Default;
                lyrics = [.. LrcHelper.ParseText(encoding.GetString(buffer))];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        }

        return new AudioMetaArtifacts(cover, lyrics);
    }

    private static BitmapSource DecodeCover(byte[] coverBytes)
    {
        var cover = CoverDataExtractor.Extract(coverBytes);

        if (cover is { CanFreeze: true, IsFrozen: false })
            cover.Freeze();

        return cover;
    }

    private static byte[] FindEmbeddedPicture(TagLib.File file)
    {
        foreach (var picture in file.Tag?.Pictures ?? [])
        {
            var data = picture?.Data?.Data;
            if (data is { Length: > 0 })
                return data;
        }

        return null;
    }

    private static string FindEmbeddedLyrics(TagLib.File file)
    {
        var lyrics = file.Tag?.Lyrics;
        if (!string.IsNullOrWhiteSpace(lyrics))
            return lyrics;

        // ffmpeg and a few other taggers write lyrics to a TXXX frame
        // ("LYRICS", "UNSYNCEDLYRICS" or even "USLT") instead of the standard
        // USLT frame that TagLib maps to Tag.Lyrics.
        if (file.GetTag(TagLib.TagTypes.Id3v2) is not TagLib.Id3v2.Tag id3)
            return null;

        foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
        {
            var description = frame.Description ?? string.Empty;
            if (!description.Contains("LYRIC", StringComparison.OrdinalIgnoreCase) &&
                !description.Equals("USLT", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var text in frame.Text ?? [])
            {
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return null;
    }

    private void ApplyAudioArtifacts(string path, AudioMetaArtifacts artifacts, int version)
    {
        if (_disposed || _quiesced || HasVideo)
            return;

        if (version != Volatile.Read(ref _metaVersion) ||
            !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            return;

        CoverArt = artifacts?.Cover;

        StopLyrics();
        _lyricLines = artifacts?.Lyrics;

        if (_lyricLines != null && _lyricLines.Length != 0)
        {
            _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _lyricTimer.Tick += (sender, e) =>
            {
                if (!_disposed && !_quiesced &&
                    mediaElement != null &&
                    _lyricLines != null && _lyricLines.Length != 0)
                {
                    var lyric = LrcHelper.GetNearestLrc(_lyricLines, new TimeSpan(mediaElement.MediaPosition));
                    metaLyric.Text = lyric?.LrcText?.Trim();
                }
                else
                {
                    metaLyric.Text = null;
                    metaLyric.Visibility = Visibility.Collapsed;
                }
            };
            _lyricTimer.Start();

            metaLyric.Visibility = Visibility.Visible;
        }
        else
        {
            metaLyric.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelPendingMeta()
    {
        var cancellation = Interlocked.Exchange(ref _metaCancellation, null);
        if (cancellation == null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The background load finished between the exchange and Cancel.
        }
    }

    private void StopLyrics()
    {
        _lyricTimer?.Stop();
        _lyricTimer = null;
        _lyricLines = null;
        metaLyric.Text = null;
        metaLyric.Visibility = Visibility.Collapsed;
    }

    private sealed record AudioMetaArtifacts(BitmapSource Cover, LrcLine[] Lyrics);

    public double LinearVolume
    {
        get => mediaElement.Volume;
        set
        {
            mediaElement.Volume = value;
            OnPropertyChanged();
        }
    }

    private void ChangeVolume(double delta)
    {
        LinearVolume = Math.Max(0d, Math.Min(1d, LinearVolume + delta));
    }

    private void TogglePlayPause(object sender, EventArgs e)
    {
        if (mediaElement.IsPlaying)
            mediaElement.Pause();
        else
            mediaElement.Play();
    }

    private void ToggleShouldLoop(object sender, EventArgs e)
    {
        ShouldLoop = !ShouldLoop;
    }

    private void ToggleHardwareAcceleration(object sender, EventArgs e)
    {
        UseHardwareAcceleration = !UseHardwareAcceleration;
        SettingHelper.Set("UseHardwareAcceleration", UseHardwareAcceleration, "QuickLook.Plugin.VideoViewer");
        HardwareAccelerationModeChanged(UseHardwareAcceleration);
    }

    private void HardwareAccelerationModeChanged(bool enable)
    {
        try
        {
            var player = mediaElement?.MediaUriPlayer;
            if (player == null) return;

            if (mediaElement.Source == null)
            {
                // No source loaded yet – just store the flag for the next Open
                player.Dispatcher.BeginInvoke(() =>
                    player.EnableLAVHardwareAcceleration = enable);
                return;
            }

            // Dispatch to the player's own MTA thread.
            // ApplyHardwareAcceleration will call OpenSource() there, which
            // rebuilds the full graph (incl. EVR/VMR9 allocator) so that
            // NewAllocatorSurface fires and the WPF back buffer is refreshed.
            // Position + play state are restored inside ApplyHardwareAcceleration
            // via a MediaOpened callback.
            player.Dispatcher.BeginInvoke(() =>
                player.ApplyHardwareAcceleration(enable));
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public void LoadAndPlay(string path, MediaInfoSnapshot info)
    {
        _timingPath = path;
        _loadClock = VideoTiming.Begin();
        _revealGate.Reset();
        CancelReveal();

        var isNewMedia = !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);
        _path = path;

        // v1.2.15: know upfront whether this is a video, so the audio cover
        // panel (music note + tags) never flashes while the playback graph is
        // being built - it would render as a gray area until MediaOpened.
        HasVideo = info?.HasVideo == true;

        // Never let the previous track's cover linger while this file's tags
        // are decoded in the background.
        if (isNewMedia && !HasVideo)
            CoverArt = null;

        // v1.2.15: show the shell thumbnail while the media opens so the start
        // of a video preview has no blank/gray loading surface.
        if (HasVideo)
            LoadThumbnailAsync(path);
        else
            PublishPanel();

        // Detect whether it is other playback formats
        if (!HasVideo)
        {
            if (info?.AudioCodec?.Equals("MIDI", StringComparison.OrdinalIgnoreCase) ?? false)
            {
                _midiPlayer = new MidiPlayer(this, _context);
                _midiPlayer.LoadAndPlay(path);
                return; // Midi player will handle the playback at all
            }
        }

        // v1.2.13: metadata is deferred to MediaOpened so it never blocks the
        // first frame of the media.
        _pendingMetaPath = path;
        _pendingMetaInfo = info;

        // detect rotation (already normalized in the MediaInfo snapshot)
        ApplyRotation(info?.Rotation ?? 0d);

        mediaElement.Source = new Uri(path);
        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        LinearVolume = Math.Max(0d, Math.Min(1d, SettingHelper.Get("VolumeDouble", 1d, "QuickLook.Plugin.VideoViewer")));

        mediaElement.Play();
        VideoTiming.Write("graph-play-return", path, _loadClock?.ElapsedMilliseconds ?? 0);
    }

    /// <summary>
    /// Applies the result of the background MediaInfo probe. The panel is
    /// already visible and may already be playing; only the parts that needed
    /// the native header read are updated here.
    /// </summary>
    public void ObserveDeferredInfo(string path, Task<MediaInfoSnapshot> infoTask)
    {
        if (infoTask == null)
            return;

        _ = ObserveDeferredInfoAsync(path, infoTask);
    }

    private async Task ObserveDeferredInfoAsync(string path, Task<MediaInfoSnapshot> infoTask)
    {
        MediaInfoSnapshot info;
        try
        {
            info = await infoTask.ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        if (info == null || info.IsEstimated || _disposed || _quiesced || _mediaFailed)
            return;

        try
        {
            _ = Dispatcher.BeginInvoke(
                new Action(() => ApplyDeferredInfo(path, info)),
                DispatcherPriority.Background);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private void ApplyDeferredInfo(string path, MediaInfoSnapshot info)
    {
        if (_disposed || _quiesced || _mediaFailed)
            return;

        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            return;

        // A container with neither stream is a failed read, not new information
        // about the preview.
        if (!info.HasVideo && !info.HasAudio)
            return;

        // Once playback has opened, the playback graph has the final say on
        // whether there is a video stream (MediaInfo can miss unusual
        // containers). Before that, trust the probe for the panel choice.
        var hasVideo = _mediaSettled ? mediaElement?.HasVideo == true : info.HasVideo;
        HasVideo = hasVideo;

        if (hasVideo)
        {
            ApplyRotation(info.Rotation);
        }
        else
        {
            _revealGate.TryTakeReveal();
            CancelReveal();
            videoThumbnail.Source = null;
            videoThumbnail.Visibility = Visibility.Collapsed;
        }

        _pendingMetaPath = path;
        _pendingMetaInfo = info;

        if (!hasVideo && (_mediaSettled || _midiPlayer != null))
        {
            UpdateMeta(path, info);
            _pendingMetaPath = null;
            _pendingMetaInfo = null;
        }

        if (hasVideo)
        {
            if (info.HasVideo)
                FitWindowToInfo(info);
            else
                FitWindowToPlayer();
        }
        else
        {
            FitAudioWindow();
        }
    }

    private void ApplyRotation(double rotation)
    {
        mediaElement.LayoutTransform = Math.Abs(rotation) > 0.1d
            ? new RotateTransform(rotation, 0.5d, 0.5d)
            : null;
    }

    private void FitWindowToInfo(MediaInfoSnapshot info)
    {
        if (info is not { HasVideo: true })
        {
            FitAudioWindow();
            return;
        }

        if (info.Width <= 0 || info.Height <= 0)
        {
            FitWindowToPlayer();
            return;
        }

        FitVideoWindow(info.Width, info.Height, info.Rotation);
    }

    private void FitWindowToPlayer()
    {
        try
        {
            var player = mediaElement?.MediaUriPlayer;
            if (player == null)
                return;

            FitVideoWindow(player.NaturalVideoWidth, player.NaturalVideoHeight, 0d);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    private void FitVideoWindow(int width, int height, double rotation)
    {
        if (width <= 0 || height <= 0)
            return;

        if (Math.Abs(rotation) % 180 > 0.1d)
            (width, height) = (height, width);

        var before = _context.PreferredSize;
        _context.SetPreferredSizeFit(new Size(width, height), 0.8d);
        _context.CanResize = true;

        if (_context.PreferredSize != before)
            _context.ApplyPreferredSizeNow();
    }

    private void FitAudioWindow()
    {
        var before = _context.PreferredSize;
        _context.PreferredSize = new Size(500, 300);
        _context.CanResize = false;

        if (_context.PreferredSize != before)
            _context.ApplyPreferredSizeNow();
    }

    private void LoadThumbnailAsync(string path)
    {
        var clock = VideoTiming.Begin();
        var thumbnail = ThumbnailPrefetch.GetOrStart(path);
        _ = Task.Run(async () =>
        {
            try
            {
                var source = await thumbnail.ConfigureAwait(false);
                if (source == null)
                {
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_disposed || _quiesced)
                            return;

                        _thumbnailUnavailable = true;
                        VideoTiming.Write("thumbnail-unavailable", path, clock?.ElapsedMilliseconds ?? 0);

                        // With no thumbnail, the first presented frame is the
                        // other safe moment to attach the panel. If that does
                        // not arrive (some graphs only start after attachment),
                        // this delayed deadline keeps playback reachable.
                        if (_mediaSettled && !_published)
                            StartPublishDeadline();
                    }));
                    return;
                }

                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    VideoTiming.Write(
                        $"thumbnail-callback ui={Environment.CurrentManagedThreadId}",
                        path,
                        clock?.ElapsedMilliseconds ?? 0);

                    // A thumbnail that finishes after the live surface was
                    // revealed (or after a failure) must not cover it. While
                    // the surface is still waiting for its first frame, a late
                    // thumbnail is exactly what should be shown.
                    if (_disposed || _quiesced || _revealGate.IsRevealed)
                    {
                        VideoTiming.Write(
                            $"thumbnail-skip disposed={_disposed} quiesced={_quiesced} revealed={_revealGate.IsRevealed}",
                            path,
                            clock?.ElapsedMilliseconds ?? 0);
                        return;
                    }

                    videoThumbnail.Source = source;
                    videoThumbnail.Visibility = Visibility.Visible;
                    VideoTiming.Write("thumbnail-shown", path, clock?.ElapsedMilliseconds ?? 0);

                    // The panel was built in the hidden preload slot; publish
                    // it now that it has a picture instead of a black surface.
                    PublishPanel();
                }));

                VideoTiming.Write("thumbnail-ready", path, clock?.ElapsedMilliseconds ?? 0);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        });
    }

    /// <summary>
    /// Called by the media player on its own thread for every presented frame.
    /// Only the first frame after MediaOpened does any work.
    /// </summary>
    private void PlayerFramePresented()
    {
        if (_disposed || _quiesced || !_revealGate.NotifyFrame())
            return;

        VideoTiming.Write(
            "first-frame",
            _timingPath,
            _loadClock?.ElapsedMilliseconds ?? 0);

        try
        {
            Dispatcher.BeginInvoke(new Action(BeginThumbnailReveal), DispatcherPriority.Render);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    /// <summary>
    /// Releases the thumbnail at most once. The live surface is already visible
    /// underneath, so removing the thumbnail after a real frame cannot show an
    /// empty renderer surface.
    /// </summary>
    private void BeginThumbnailReveal()
    {
        if (_disposed || _quiesced)
            return;

        // A renderer may present its first frame before a thumbnail (or no
        // thumbnail at all) is available. At that point the panel is the only
        // thing that can show the video, so attach it before dropping the
        // cover image.
        if (!_published)
            PublishPanel();

        if (!_revealGate.TryTakeReveal())
            return;

        VideoTiming.Write(
            $"reveal-check|visibility={videoThumbnail.Visibility}|source={(videoThumbnail.Source != null)}",
            _timingPath,
            _loadClock?.ElapsedMilliseconds ?? 0);

        CancelReveal();

        if (videoThumbnail.Visibility != Visibility.Visible || videoThumbnail.Source == null)
        {
            ClearThumbnail();
            return;
        }

        // The frame notification is raised from the presenter thread. Let the
        // compositor draw at least one frame with the live surface underneath
        // the thumbnail; hiding it in the same dispatcher turn can still catch
        // the previous (empty) composition.
        var frames = 0;
        EventHandler handler = null;
        handler = (_, _) =>
        {
            if (++frames < 2)
                return;

            FinishReveal();
        };

        _revealCompositionHandler = handler;
        CompositionTarget.Rendering += handler;

        // An occluded or minimized window may not compose at all; never leave
        // the thumbnail or the handler behind.
        _revealFallbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _revealFallbackTimer.Tick += (_, _) => FinishReveal();
        _revealFallbackTimer.Start();
    }

    private void StartRevealFallback()
    {
        CancelReveal();

        _revealFallbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(VideoRevealGate.DefaultFrameTimeoutMilliseconds)
        };
        _revealFallbackTimer.Tick += RevealFallbackTick;
        _revealFallbackTimer.Start();
    }

    /// <summary>
    /// Attaches the panel to the window for the first time. Until now the host
    /// kept the previous preview (or its own backdrop) on screen, so the black
    /// surface of a panel whose media graph is still opening is never visible.
    /// </summary>
    private void PublishPanel()
    {
        if (_published || _disposed || _quiesced)
            return;

        _published = true;
        CancelReveal();

        _context.ViewerContent = this;
        _context.IsBusy = false;

        VideoTiming.Write("panel-published", _timingPath, _loadClock?.ElapsedMilliseconds ?? 0);

        if (_revealGate.IsWaitingForFrame)
            StartRevealFallback();
    }

    private void StartPublishDeadline()
    {
        CancelReveal();

        _revealFallbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(VideoRevealGate.DefaultFrameTimeoutMilliseconds)
        };
        _revealFallbackTimer.Tick += (_, _) => PublishPanel();
        _revealFallbackTimer.Start();
    }

    private void RevealFallbackTick(object sender, EventArgs e)
    {
        if (!_revealGate.IsFrameTimeoutElapsed(Environment.TickCount64))
            return;

        VideoTiming.Write("first-frame-timeout", _timingPath, _loadClock?.ElapsedMilliseconds ?? 0);
        BeginThumbnailReveal();
    }

    private void FinishReveal()
    {
        CancelReveal();
        ClearThumbnail();
        VideoTiming.Write("thumbnail-revealed", _timingPath, _loadClock?.ElapsedMilliseconds ?? 0);
    }

    private void ClearThumbnail()
    {
        videoThumbnail.Source = null;
        videoThumbnail.Visibility = Visibility.Collapsed;
    }

    private void CancelReveal()
    {
        if (_revealFallbackTimer != null)
        {
            _revealFallbackTimer.Stop();
            _revealFallbackTimer = null;
        }

        var handler = _revealCompositionHandler;
        _revealCompositionHandler = null;
        if (handler != null)
            CompositionTarget.Rendering -= handler;
    }

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
