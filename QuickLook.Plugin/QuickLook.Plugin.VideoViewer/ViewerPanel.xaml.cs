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

    private bool _hasVideo;
    private bool _isPlaying;
    private bool _wasPlaying;
    private bool _shouldLoop;
    private bool _useHardwareAcceleration;
    private bool _disposed;
    private bool _mediaSettled;
    private string _pendingMetaPath;
    private MediaInfoSnapshot _pendingMetaInfo;

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
            if (value == null) return;
            _coverArt = value;
            OnPropertyChanged();
        }
    }

    public void Dispose()
    {
        _disposed = true;

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

        _lyricTimer?.Stop();
        _lyricTimer = null;
        _lyricLines = null;
        _midiPlayer?.Dispose();
        _midiPlayer = null;
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

        // v1.2.36: the media is (or failed to become) ready - a slow shell
        // thumbnail task that completes after this point must not cover the
        // playing surface with a stale thumbnail.
        _mediaSettled = true;

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
        if (mediaElement == null)
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

        try
        {
            if (info == null)
                throw new NullReferenceException();

            var title = info.Title;
            var artist = info.Artist;
            var album = info.Album;

            metaTitle.Text = !string.IsNullOrWhiteSpace(title) ? title : Path.GetFileName(path);
            metaArtists.Text = artist;
            metaAlbum.Text = album;

            // Extract cover art
            var coverData = info.CoverData;
            var coverBytes = CoverDataExtractor.Extract(coverData);
            CoverArt = CoverDataExtractor.Extract(coverBytes);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            metaTitle.Text = Path.GetFileName(path);
            metaArtists.Text = metaAlbum.Text = string.Empty;
        }

        metaArtists.Visibility = string.IsNullOrEmpty(metaArtists.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        metaAlbum.Visibility = string.IsNullOrEmpty(metaAlbum.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var lyricPath = Path.ChangeExtension(path, ".lrc");

        // Stop previous timer if any.
        _lyricTimer?.Stop();
        _lyricTimer = null;
        _lyricLines = null;

        if (File.Exists(lyricPath))
        {
            var buffer = File.ReadAllBytes(lyricPath);
            var encoding = CharsetDetector.DetectFromBytes(buffer).Detected?.Encoding ?? Encoding.Default;

            _lyricLines = [.. LrcHelper.ParseText(encoding.GetString(buffer))];
        }
        else
        {
            // Use embedded lyrics from MediaInfo if present.
            // Common tag: General/Lyrics (may contain LRC formatted content).
            var embeddedLyrics = info.Lyrics;

            // Only check whether the tag of lyrics is present by MediaInfo
            if (!string.IsNullOrWhiteSpace(embeddedLyrics))
            {
                var file = TagLib.File.Create(path);
                embeddedLyrics = file.Tag.Lyrics;

                // Check whether the tag of lyrics is present by TagLib#
                if (!string.IsNullOrWhiteSpace(embeddedLyrics))
                {
                    _lyricLines = [.. LrcHelper.ParseText(embeddedLyrics)];
                }
            }
        }

        if (_lyricLines != null && _lyricLines.Length != 0)
        {
            _lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _lyricTimer.Tick += (sender, e) =>
            {
                if (_lyricLines != null && _lyricLines.Length != 0)
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
        // v1.2.15: know upfront whether this is a video, so the audio cover
        // panel (music note + tags) never flashes while the playback graph is
        // being built - it would render as a gray area until MediaOpened.
        HasVideo = info?.HasVideo == true;

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
        var rotation = info?.Rotation ?? 0d;
        if (Math.Abs(rotation) > 0.1d)
            mediaElement.LayoutTransform = new RotateTransform(rotation, 0.5d, 0.5d);

        mediaElement.Source = new Uri(path);
        // old plugin use an int-typed "Volume" config key ranged from 0 to 100. Let's use a new one here.
        LinearVolume = Math.Max(0d, Math.Min(1d, SettingHelper.Get("VolumeDouble", 1d, "QuickLook.Plugin.VideoViewer")));

        mediaElement.Play();
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
                    if (_disposed || _mediaSettled)
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
