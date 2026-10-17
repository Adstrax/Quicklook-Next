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
using QuickLook.Common.ExtensionMethods;
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

        videoThumbnail.Source = null;
        videoThumbnail.Visibility = Visibility.Collapsed;

        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        SettingHelper.Set("VolumeDouble", LinearVolume, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("ShouldLoop", ShouldLoop, "QuickLook.Plugin.VideoViewer");
        SettingHelper.Set("UseHardwareAcceleration", UseHardwareAcceleration, "QuickLook.Plugin.VideoViewer");

        try
        {
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

        // v1.2.36: the media is (or failed to become) ready - a slow shell
        // thumbnail task that completes after this point must not cover the
        // playing surface with a stale thumbnail.

        // v1.2.15: the playback graph is ready. Reveal the surface under the
        // thumbnail and keep the thumbnail on top for a short grace period, so
        // the renderer's blank surface (gray) is never visible before its first
        // frame paints.
        mediaElement.Visibility = Visibility.Visible;

        if (videoThumbnail.Visibility == Visibility.Visible)
        {
            Task.Delay(250).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed)
                    return;

                videoThumbnail.Source = null;
                videoThumbnail.Visibility = Visibility.Collapsed;
            })));
        }
        else
        {
            videoThumbnail.Source = null;
            videoThumbnail.Visibility = Visibility.Collapsed;
        }

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

        _context.IsBusy = false;

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

            videoThumbnail.Source = null;
            videoThumbnail.Visibility = Visibility.Collapsed;

            ProcessHelper.WriteLog($"Video preview failed for \"{mediaElement?.Source}\": {e.Exception}");

            var translationFile = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Translations.config");

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
        _ = Task.Run(() =>
        {
            try
            {
                var scale = DisplayDeviceHelper.GetCurrentScaleFactor();
                using var thumb = WindowsThumbnailProvider.GetThumbnail(path,
                    (int)(640 * scale.Horizontal), (int)(360 * scale.Vertical), ThumbnailOptions.ScaleUp);
                if (thumb == null)
                    return;

                var source = thumb.ToBitmapSource();
                source.Freeze();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // v1.2.36: if the media already opened (or failed) while
                    // the thumbnail was still being extracted, the playing
                    // surface / error text is already visible - do not cover
                    // it with a late thumbnail.
                    if (_disposed || _quiesced || _mediaSettled)
                        return;

                    videoThumbnail.Source = source;
                    videoThumbnail.Visibility = Visibility.Visible;
                }));
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        });
    }

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
