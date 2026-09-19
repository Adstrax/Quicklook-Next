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

using Microsoft.Win32;
using QuickLook.Common.Annotations;
using QuickLook.Common.ExtensionMethods;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.ImageViewer.Helpers;
using QuickLook.Plugin.ImageViewer.NativeMethods;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuickLook.Plugin.ImageViewer;

public partial class ImagePanel : UserControl, INotifyPropertyChanged, IDisposable
{
    private Visibility _backgroundVisibility = Visibility.Visible;
    private ContextObject _contextObject;
    private Point? _dragInitPos;
    private Uri _imageSource;
    private bool _isZoomFactorFirstSet = true;
    private DateTime _lastZoomTime = DateTime.MinValue;
    private double _maxZoomFactor = 3d;
    private MetaProvider _meta;
    private double _minZoomFactor = 0.1d;
    private BitmapScalingMode _renderMode = BitmapScalingMode.Linear;
    private bool _showZoomLevelInfo = true;
    private BitmapSource _source;
    private double _zoomFactor = 1d;

    private bool _zoomToFit = true;
    private double _zoomToFitFactor;
    private bool _zoomWithControlKey;

    private Visibility _copyIconVisibility = Visibility.Visible;
    private Visibility _saveAsVisibility = Visibility.Collapsed;
    private Visibility _reverseColorVisibility = Visibility.Collapsed;
    private Visibility _metaIconVisibility = Visibility.Visible;
    private bool _contentReadyFired;
    private DispatcherTimer _readyTimer;

    public ImagePanel()
    {
        InitializeComponent();

        Resources.MergedDictionaries.Clear();

        buttonCopy.Click += OnCopyOnClick;

        buttonSaveAs.Click += OnSaveAsOnClick;

        buttonReverseColor.Click += OnReverseColorOnClick;

        buttonMeta.Click += (sender, e) =>
            textMeta.Visibility = textMeta.Visibility == Visibility.Collapsed
                ? Visibility.Visible
                : Visibility.Collapsed;

        buttonBackgroundColour.Click += OnBackgroundColourOnClick;

        SizeChanged += ImagePanel_SizeChanged;
        viewPanelImage.DoZoomToFit += (sender, e) => DoZoomToFit();
        viewPanelImage.ImageLoaded += (sender, e) =>
        {
            // v1.2.14: signal content readiness BEFORE IsBusy flips so the
            // viewer window can swap the pending content in atomically.
            FireContentReady();
            if (ContextObject != null)
                ContextObject.IsBusy = false;

            // v1.2.13: re-fit once layout settles. The zoom-to-fit computed at
            // source-arrival time can run before the panel has its final size
            // (window still resizing on a switch), which would leave the image
            // unfitted with blank space around it.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ZoomToFit && viewPanel.ActualWidth > 0 && viewPanel.ActualHeight > 0)
                    DoZoomToFit();
            }), DispatcherPriority.Render);
        };

        // v1.2.14: if the first frame never arrives (slow or failed decode),
        // swap the content in after a timeout so the window is not stuck on the
        // previous preview forever.
        _readyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _readyTimer.Tick += (_, _) =>
        {
            if (FireContentReady() && ContextObject != null)
                ContextObject.IsBusy = false;
        };
        _readyTimer.Start();

        viewPanel.PreviewMouseWheel += ViewPanel_PreviewMouseWheel;
        viewPanel.MouseLeftButtonDown += ViewPanel_MouseLeftButtonDown;
        viewPanel.MouseMove += ViewPanel_MouseMove;
        viewPanel.MouseDoubleClick += ViewPanel_MouseDoubleClick;

        viewPanel.ManipulationInertiaStarting += ViewPanel_ManipulationInertiaStarting;
        viewPanel.ManipulationStarting += ViewPanel_ManipulationStarting;
        viewPanel.ManipulationDelta += ViewPanel_ManipulationDelta;
    }

    internal ImagePanel(ContextObject context, MetaProvider meta) : this()
    {
        ContextObject = context;
        Meta = meta;

        _ = meta.GetSize();

        LoadMetaAsync();
        Theme = ContextObject.Theme;
    }

    public bool ZoomWithControlKey
    {
        get => _zoomWithControlKey;
        set
        {
            _zoomWithControlKey = value;
            OnPropertyChanged();
        }
    }

    public bool ShowZoomLevelInfo
    {
        get => _showZoomLevelInfo;
        set
        {
            if (value == _showZoomLevelInfo) return;
            _showZoomLevelInfo = value;
            OnPropertyChanged();
        }
    }

    public Themes Theme
    {
        get => ContextObject?.Theme ?? Themes.Dark;
        set
        {
            ContextObject.Theme = value;
            OnPropertyChanged();
        }
    }

    public BitmapScalingMode RenderMode
    {
        get => _renderMode;
        set
        {
            _renderMode = value;
            OnPropertyChanged();
        }
    }

    public bool ZoomToFit
    {
        get => _zoomToFit;
        set
        {
            _zoomToFit = value;
            OnPropertyChanged();
        }
    }

    public Visibility CopyIconVisibility
    {
        get => _copyIconVisibility;
        set
        {
            _copyIconVisibility = value;
            OnPropertyChanged();
        }
    }

    public Visibility SaveAsVisibility
    {
        get => _saveAsVisibility;
        set
        {
            _saveAsVisibility = value;
            OnPropertyChanged();
        }
    }

    public Visibility ReverseColorVisibility
    {
        get => _reverseColorVisibility;
        set
        {
            _reverseColorVisibility = value;
            OnPropertyChanged();
        }
    }

    public Visibility MetaIconVisibility
    {
        get => _metaIconVisibility;
        set
        {
            _metaIconVisibility = value;
            OnPropertyChanged();
        }
    }

    public Visibility BackgroundVisibility
    {
        get => _backgroundVisibility;
        set
        {
            _backgroundVisibility = value;
            OnPropertyChanged();
        }
    }

    public double MinZoomFactor
    {
        get => _minZoomFactor;
        set
        {
            _minZoomFactor = value;
            OnPropertyChanged();
        }
    }

    public double MaxZoomFactor
    {
        get => _maxZoomFactor;
        set
        {
            _maxZoomFactor = value;
            OnPropertyChanged();
        }
    }

    public double ZoomToFitFactor
    {
        get => _zoomToFitFactor;
        private set
        {
            _zoomToFitFactor = value;
            OnPropertyChanged();
        }
    }

    public double ZoomFactor
    {
        get => _zoomFactor;
        private set
        {
            _zoomFactor = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ZoomDisplayFactor));

            if (_isZoomFactorFirstSet)
            {
                _isZoomFactorFirstSet = false;
                return;
            }
        }
    }

    /// <summary>
    /// v5.0.5: what the zoom badge shows. Zoom lives in the decoded bitmap's pixel space, and a
    /// very large image is decoded below its real size (see <c>DecodePixelLimit</c>) - scaling by
    /// decoded/real keeps the badge truthful about the file instead of about the downscaled copy.
    /// </summary>
    public double ZoomDisplayFactor => ZoomFactor * DecodeScale;

    /// <summary>Decoded size / real image size; 1 for an image decoded at full resolution.</summary>
    private double DecodeScale
    {
        get
        {
            var real = _meta?.GetSize() ?? default;
            var source = viewPanelImage?.Source;
            if (source == null || real.Width <= 0 || real.Height <= 0)
                return 1d;

            var scale = Math.Min(source.Width / real.Width, source.Height / real.Height);
            return double.IsNaN(scale) || scale <= 0 ? 1d : Math.Min(1d, scale);
        }
    }

    public Uri ImageUriSource
    {
        get => _imageSource;
        set
        {
            _imageSource = value;

            OnPropertyChanged();
        }
    }

    public BitmapSource Source
    {
        get => _source;
        set
        {
            _source = value;
            OnPropertyChanged();

            if (ImageUriSource == null)
                viewPanelImage.Source = _source;
        }
    }

    public ContextObject ContextObject
    {
        get => _contextObject;
        set
        {
            _contextObject = value;
            OnPropertyChanged();
        }
    }

    public MetaProvider Meta
    {
        get => _meta;
        set
        {
            if (Equals(value, _meta)) return;
            _meta = value;
            OnPropertyChanged();
        }
    }

    public void Dispose()
    {
        _readyTimer?.Stop();
        _readyTimer = null;
        viewPanelImage?.Dispose();
        viewPanelImage = null;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnCopyOnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_source is not null)
            {
                ClipboardEx.SetClipboardImage(_source);
                return;
            }

            if (viewPanelImage.Source is BitmapSource bitmapSource)
            {
                ClipboardEx.SetClipboardImage(bitmapSource);
                return;
            }
        }
        catch
        {
            ///
        }
    }

    private void OnSaveAsOnClick(object sender, RoutedEventArgs e)
    {
        if (_source == null)
        {
            return;
        }

        var dialog = new SaveFileDialog()
        {
            Filter = "PNG Image|*.png",
            DefaultExt = ".png",
            FileName = Path.GetFileNameWithoutExtension(ContextObject.Title)
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                if (File.Exists(dialog.FileName))
                {
                    File.Delete(dialog.FileName);
                }

                PngBitmapEncoder encoder = new();
                encoder.Frames.Add(BitmapFrame.Create(_source));
                using FileStream stream = new(dialog.FileName, FileMode.Create, FileAccess.Write);
                encoder.Save(stream);
            }
            catch
            {
                ///
            }
        }
    }

    private void OnReverseColorOnClick(object sender, RoutedEventArgs e)
    {
        if (_source == null)
        {
            return;
        }

        Source = _source.InvertColors();
    }

    private void OnBackgroundColourOnClick(object sender, RoutedEventArgs e)
    {
        Theme = Theme == Themes.Dark ? Themes.Light : Themes.Dark;

        SettingHelper.Set("LastTheme", (int)Theme, "QuickLook.Plugin.ImageViewer");
    }

    /// <summary>
    /// v1.2.13: build the metadata panel on a background thread so the native
    /// exiv2 call no longer sits between the window showing and the first image
    /// frame (the spinner). The text is applied to the UI once ready.
    /// </summary>
    private void LoadMetaAsync()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var entries = Meta.GetExif().Values
                    .Where(m => !string.IsNullOrWhiteSpace(m.Item1) && !string.IsNullOrWhiteSpace(m.Item2))
                    .Select(m => (Label: m.Item1, Value: m.Item2))
                    .ToList();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    textMeta.Inlines.Clear();
                    foreach (var (label, value) in entries)
                    {
                        textMeta.Inlines.Add(new Run(label) { FontWeight = FontWeights.SemiBold });
                        textMeta.Inlines.Add(": ");
                        textMeta.Inlines.Add(value);
                        textMeta.Inlines.Add("\r\n");
                    }

                    if (textMeta.Inlines.Count > 0)
                        textMeta.Inlines.Remove(textMeta.Inlines.LastInline);
                    else
                        MetaIconVisibility = Visibility.Collapsed;
                }), DispatcherPriority.Background);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
            }
        });
    }

    public event EventHandler<int> ImageScrolled;

    public event EventHandler ZoomChanged;

    /// <summary>
    /// v1.2.14: fired once when the first image frame is decoded (or after the
    /// readiness timeout), letting the viewer swap this panel in without a
    /// blank loading state.
    /// </summary>
    public event Action ContentReady;

    private bool FireContentReady()
    {
        if (_contentReadyFired)
            return false;

        _contentReadyFired = true;
        _readyTimer?.Stop();
        ContentReady?.Invoke();
        return true;
    }

    private void ImagePanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateZoomToFitFactor();

        if (ZoomToFit)
            DoZoomToFit();
    }

    private void ViewPanel_ManipulationInertiaStarting(object sender, ManipulationInertiaStartingEventArgs e)
    {
        e.TranslationBehavior = new InertiaTranslationBehavior
        {
            InitialVelocity = e.InitialVelocities.LinearVelocity,
            DesiredDeceleration = 10d * 96d / (1000d * 1000d)
        };
    }

    private void ViewPanel_ManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = viewPanel;
        e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
    }

    private void ViewPanel_ManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        var delta = e.DeltaManipulation;

        var newZoom = ZoomFactor + ZoomFactor * (delta.Scale.X - 1);

        Zoom(newZoom);

        viewPanel.ScrollToHorizontalOffset(viewPanel.HorizontalOffset - delta.Translation.X);
        viewPanel.ScrollToVerticalOffset(viewPanel.VerticalOffset - delta.Translation.Y);

        e.Handled = true;
    }

    private void ViewPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.MouseDevice.Capture(viewPanel);

        _dragInitPos = e.GetPosition(viewPanel);
        var temp = _dragInitPos.Value; // Point is a type value
        temp.Offset(viewPanel.HorizontalOffset, viewPanel.VerticalOffset);
        _dragInitPos = temp;
    }

    private void ViewPanel_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        DoZoomToFit();
    }

    private void ViewPanel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragInitPos.HasValue)
            return;

        if (e.LeftButton == MouseButtonState.Released)
        {
            e.MouseDevice.Capture(null);

            _dragInitPos = null;
            return;
        }

        e.Handled = true;

        var delta = _dragInitPos.Value - e.GetPosition(viewPanel);

        viewPanel.ScrollToHorizontalOffset(delta.X);
        viewPanel.ScrollToVerticalOffset(delta.Y);
    }

    private void ViewPanel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;

        // normal scroll when Control is not pressed, useful for PdfViewer
        if (ZoomWithControlKey && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            viewPanel.ScrollToVerticalOffset(viewPanel.VerticalOffset - e.Delta);
            ImageScrolled?.Invoke(this, e.Delta);
            return;
        }

        // otherwise, perform normal zooming
        var newZoom = ZoomFactor + ZoomFactor * e.Delta / 120 * 0.1;

        Zoom(newZoom);
    }

    public Size GetScrollSize()
    {
        return new Size(viewPanel.ScrollableWidth, viewPanel.ScrollableHeight);
    }

    public Point GetScrollPosition()
    {
        return new Point(viewPanel.HorizontalOffset, viewPanel.VerticalOffset);
    }

    public void SetScrollPosition(Point point)
    {
        viewPanel.ScrollToHorizontalOffset(point.X);
        viewPanel.ScrollToVerticalOffset(point.Y);
    }

    public void DoZoomToFit()
    {
        if (viewPanel.ActualWidth <= 0 || viewPanel.ActualHeight <= 0)
            return;

        UpdateZoomToFitFactor();

        Zoom(ZoomToFitFactor, false, true);
    }

    private void UpdateZoomToFitFactor()
    {
        if (viewPanelImage?.Source == null)
        {
            ZoomToFitFactor = 1d;
            return;
        }

        if (viewPanel.ActualWidth <= 0 || viewPanel.ActualHeight <= 0)
            return; // layout not ready yet; SizeChanged will re-run this

        var factor = Math.Min(viewPanel.ActualWidth / viewPanelImage.Source.Width,
            viewPanel.ActualHeight / viewPanelImage.Source.Height);

        ZoomToFitFactor = factor;
    }

    public void ResetZoom()
    {
        ZoomToFitFactor = 1;
        Zoom(1d, true, ZoomToFit);
    }

    public void Zoom(double factor, bool suppressEvent = false, bool isToFit = false)
    {
        if (viewPanelImage?.Source == null)
            return;

        // pause when fit width
        if (ZoomFactor < ZoomToFitFactor && factor > ZoomToFitFactor
            || ZoomFactor > ZoomToFitFactor && factor < ZoomToFitFactor)
        {
            factor = ZoomToFitFactor;
            ZoomToFit = true;
        }
        // pause when 100%
        else if (ZoomFactor < 1 && factor > 1 || ZoomFactor > 1 && factor < 1)
        {
            factor = 1;
            ZoomToFit = false;
        }
        else
        {
            if (!isToFit)
                ZoomToFit = false;
        }

        factor = Math.Max(factor, MinZoomFactor);
        factor = Math.Min(factor, MaxZoomFactor);

        ZoomFactor = factor;

        // v1.2.14: only show the zoom percentage badge for manual zooming
        // (wheel/pinch); automatic fit-to-window during previews/switches
        // should not flash a percentage overlay.
        if (ShowZoomLevelInfo && !isToFit && !suppressEvent)
            ((Storyboard)zoomLevelInfo.FindResource("StoryboardShowZoomLevelInfo")).Begin();

        var position = ZoomToFit
            ? new Point(viewPanelImage.Source.Width / 2, viewPanelImage.Source.Height / 2)
            : Mouse.GetPosition(viewPanelImage);

        viewPanelImage.LayoutTransform = new ScaleTransform(factor, factor);

        viewPanel.InvalidateMeasure();

        // critical for calculating offset
        viewPanel.ScrollToHorizontalOffset(0);
        viewPanel.ScrollToVerticalOffset(0);
        UpdateLayout();

        var offset = viewPanelImage.TranslatePoint(position, viewPanel) - Mouse.GetPosition(viewPanel);
        viewPanel.ScrollToHorizontalOffset(offset.X);
        viewPanel.ScrollToVerticalOffset(offset.Y);
        UpdateLayout();

        if (!suppressEvent)
            FireZoomChangedEvent();
    }

    private void FireZoomChangedEvent()
    {
        _lastZoomTime = DateTime.Now;

        Task.Delay(500).ContinueWith(t =>
        {
            if (DateTime.Now - _lastZoomTime < TimeSpan.FromSeconds(0.5))
                return;

            Debug.WriteLine($"FireZoomChangedEvent fired: {Thread.CurrentThread.ManagedThreadId}");

            Dispatcher.BeginInvoke(new Action(() => ZoomChanged?.Invoke(this, EventArgs.Empty)),
                DispatcherPriority.Background);
        });
    }

    [NotifyPropertyChangedInvocator]
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void ScrollToTop()
    {
        viewPanel.ScrollToTop();
    }

    public void ScrollToBottom()
    {
        viewPanel.ScrollToBottom();
    }
}
