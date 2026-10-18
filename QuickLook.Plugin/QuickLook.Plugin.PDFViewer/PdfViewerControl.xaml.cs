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

using PdfiumViewer;
using QuickLook.Common.ExtensionMethods;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QuickLook.Plugin.PDFViewer;

public partial class PdfViewerControl : UserControl, INotifyPropertyChanged, IDisposable
{
    private int _changePageDeltaSum;
    private bool _initPage = true;
    private double _maxZoomFactor = double.NaN;
    private double _minZoomFactor = double.NaN;

    private bool _pdfLoaded;
    private double _viewRenderFactor = double.NaN;
    private int _renderVersion;
    private int _renderedPage = -1;
    private double _renderedFactor = double.NaN;
    private bool _deferPageRender;
    private bool _awaitingFirstPage;

    // v6.1.3: pages the user has already looked at are kept as frozen bitmaps.
    // The source path is what lets a reopened PDF reuse them; a document loaded
    // from a stream has no stable identity, so it simply never hits the cache.
    private readonly PdfPageBitmapCache _pageCache = PdfPageBitmapCache.Shared;
    private string _sourcePath;

    // v6.1.3: once a page has settled, the next one is rendered ahead of time.
    // The delay is what keeps a burst of page flips from queueing a render for
    // every page the user passed through.
    private const int PrefetchDelayMilliseconds = 250;
    private int _documentGeneration;
    private int _prefetchGeneration;

    // v6.1.3: thumbnail work is demand-driven. The strip reports its visible
    // range, the scheduler cancels pages left behind by a fast scroll, and a
    // short settle delay adds only the two neighbouring pages as prefetch.
    private const int ThumbnailScrollSettleMilliseconds = 180;
    private readonly DispatcherTimer _thumbnailDemandTimer;
    private ScrollViewer _thumbnailScrollViewer;
    private VirtualizingStackPanel _thumbnailItemsPanel;
    private PdfThumbnailRequestScheduler _thumbnailRequests;
    private PdfPageRenderScheduler _pageRenders;
    private bool _thumbnailSchedulerHeldForFirstPage;
    private int _thumbnailFirstVisible = -1;
    private int _thumbnailLastVisible = -1;

    // v6.1.8: a page rendered under the pixel cap is softer than the screen.
    // Once the view settles, the visible part is rasterized again at full screen
    // density and painted over the base page. The delay keeps a drag from
    // requesting a tile per mouse move.
    private const int SharpTileSettleMilliseconds = 140;
    private const double SharpTileMargin = 0.12d;
    private const double SharpTileLeadingMargin = 0.4d;
    private readonly DispatcherTimer _sharpTileTimer;
    private int _sharpTileVersion;
    private int _sharpTilePage = -1;
    private double _sharpTileViewFactor = double.NaN;
    private Rect _sharpTileRegion = Rect.Empty;
    private int _lastTilePage = -1;
    private Rect _lastTileVisible = Rect.Empty;
    private readonly PdfTileCache _tileCache = new(maxEntries: 2, maxPixels: 32_000_000);

    /// <summary>
    /// v6.1.3: set by the plugin from the host's low memory mode. Pages are still
    /// cached inside this preview, but nothing is rendered ahead of the user.
    /// </summary>
    public bool LowMemoryMode { get; set; }

    // v6.1.2: a programmatic fit-to-window raises ZoomChanged too. Re-rendering
    // for a fit that is already satisfied replaced a correct first page with an
    // identical one and made PDFs visibly go soft before sharpening.
    private const double RenderFactorTolerance = 0.02d;

    // The host asks for the final window size from inside View(), but WPF still
    // settles a few layout details after that. Rendering the first page slightly
    // above the fit size means those last few pixels are a downscale instead of a
    // visibly soft page followed by a sharper repaint.
    private const double FirstPageOverscan = 1.25d;

    public PdfViewerControl()
    {
        InitializeComponent();

        // remove theme used in designer
        Resources.MergedDictionaries.RemoveAt(0);

        listThumbnails.SelectionChanged += UpdatePageViewWhenSelectionChanged;
        listThumbnails.Loaded += OnThumbnailListLoaded;

        _thumbnailDemandTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(ThumbnailScrollSettleMilliseconds),
        };
        _thumbnailDemandTimer.Tick += OnThumbnailDemandTimerTick;

        _sharpTileTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(SharpTileSettleMilliseconds),
        };
        _sharpTileTimer.Tick += OnSharpTileTimerTick;

        pagePanel.ZoomChanged += ReRenderCurrentPageDelayed;
        pagePanel.ImageScrolled += NavigatePage;
        pagePanel.ViewChanged += OnPageViewChanged;
    }

    public IReadOnlyList<int> PageThumbnails { get; private set; } = PdfPageIndexList.Empty;

    public int TotalPages => PdfDocumentWrapper.PdfDocument.PageCount;

    public int CurrentPage
    {
        get => listThumbnails.SelectedIndex;
        set
        {
            listThumbnails.SelectedIndex = value;
            listThumbnails.ScrollIntoView(listThumbnails.SelectedItem);

            CurrentPageChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged();
        }
    }

    public PdfDocumentWrapper PdfDocumentWrapper { get; private set; }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (pagePanel != null)
        {
            pagePanel.ZoomChanged -= ReRenderCurrentPageDelayed;
            pagePanel.ImageScrolled -= NavigatePage;
            pagePanel.ViewChanged -= OnPageViewChanged;
        }

        _sharpTileTimer.Stop();
        _sharpTileTimer.Tick -= OnSharpTileTimerTick;
        _tileCache.Clear();
        HideSharpTile();

        _pdfLoaded = false;
        _deferPageRender = false;
        _awaitingFirstPage = false;
        _renderedPage = -1;
        _renderedFactor = double.NaN;
        _sourcePath = null;
        ++_documentGeneration;
        ++_prefetchGeneration;
        ++_renderVersion;

        listThumbnails.Loaded -= OnThumbnailListLoaded;
        _thumbnailDemandTimer.Stop();
        _thumbnailDemandTimer.Tick -= OnThumbnailDemandTimerTick;
        DetachThumbnailScrollViewer();
        ReleaseThumbnailRequests();
        ReleasePageRenders();

        PdfDocumentWrapper?.Dispose();
        PdfDocumentWrapper = null;
    }

    public event PropertyChangedEventHandler PropertyChanged;

    /// <summary>
    /// v6.1.3: gives the parsed document back to the caller instead of disposing
    /// it, so the next preview of the same file version can start without
    /// copying and parsing it again. The control is unusable for that document
    /// afterwards and must still be disposed to release its UI subscriptions.
    /// </summary>
    public PdfDocumentWrapper DetachDocument()
    {
        var document = PdfDocumentWrapper;
        PdfDocumentWrapper = null;

        _pdfLoaded = false;
        _deferPageRender = false;
        _awaitingFirstPage = false;
        _renderedPage = -1;
        _renderedFactor = double.NaN;
        _sourcePath = null;
        _lastTilePage = -1;
        _lastTileVisible = Rect.Empty;

        ++_documentGeneration;
        ++_prefetchGeneration;
        ++_renderVersion;

        ReleaseThumbnailRequests();
        ReleasePageRenders();

        _sharpTileTimer.Stop();
        _tileCache.Clear();
        HideSharpTile();

        return document;
    }

    private void ReRenderCurrentPageDelayed(object sender, EventArgs e)
    {
        ReRenderCurrentPage(restoreScroll: true);
    }

    public event EventHandler CurrentPageChanged;

    /// <summary>The first page has been accepted for the current document.</summary>
    public event EventHandler FirstPagePresented;

    private void NavigatePage(object sender, int delta)
    {
        if (!_pdfLoaded)
            return;

        var pos = pagePanel.GetScrollPosition();
        var size = pagePanel.GetScrollSize();

        const double tolerance = 0.0001d;
        if (Math.Abs(pos.Y) < tolerance && delta > 0)
        {
            _changePageDeltaSum += delta;
            if (Math.Abs(_changePageDeltaSum) < 20)
                return;

            PrevPage();
            _changePageDeltaSum = 0;
        }
        else if (Math.Abs(pos.Y - size.Height) < tolerance && delta < -0)
        {
            _changePageDeltaSum += delta;
            if (Math.Abs(_changePageDeltaSum) < 20)
                return;

            NextPage();
            _changePageDeltaSum = 0;
        }
    }

    private void NextPage()
    {
        if (CurrentPage < TotalPages - 1)
        {
            CurrentPage++;
            pagePanel.ScrollToTop();
        }
    }

    private void PrevPage()
    {
        if (CurrentPage > 0)
        {
            CurrentPage--;
            pagePanel.ScrollToBottom();
        }
    }

    private void ReRenderCurrentPage(bool restoreScroll = false, bool fitOnComplete = false)
    {
        if (!_pdfLoaded)
            return;

        if (CurrentPage < 0 || CurrentPage >= TotalPages)
            return;

        Debug.WriteLine($"Re-rendering page {CurrentPage}");

        var pos = restoreScroll ? pagePanel.GetScrollPosition() : new Point();

        double factor;

        // First time showing. Set thresholds here.
        if (double.IsNaN(_minZoomFactor) || double.IsNaN(_maxZoomFactor))
        {
            factor = FitFactorFor(CurrentPage);
            _viewRenderFactor = factor;
            _minZoomFactor = 0.1 * factor;
            _maxZoomFactor = 5 * factor;
        }
        else if (pagePanel.ZoomToFit)
        {
            factor = FitFactorFor(CurrentPage);
        }
        else
        {
            // v6.1.7: _viewRenderFactor is the factor of the bitmap on screen,
            // so ZoomFactor times that factor is the view being asked for. The
            // old code overwrote the bitmap factor with this target before the
            // render; after zooming out (where the sharper bitmap is kept and no
            // render happens) the next step then used a factor the screen never
            // had, and a page switch could arrive at the wrong size.
            factor = pagePanel.ZoomFactor * _viewRenderFactor;
            factor = Math.Max(factor, _minZoomFactor);
            factor = Math.Min(factor, _maxZoomFactor);
            pagePanel.MinZoomFactor = _minZoomFactor / _viewRenderFactor;
            pagePanel.MaxZoomFactor = _maxZoomFactor / _viewRenderFactor;
        }

        var doc = PdfDocumentWrapper;
        if (doc == null)
            return;

        var page = CurrentPage;

        var targetFactor = _awaitingFirstPage ? factor * FirstPageOverscan : factor;
        if (RenderFactorIsAlreadySufficient(page, targetFactor))
        {
            _pageRenders?.CancelPending();
            if (_awaitingFirstPage)
                FinishFirstPage();
            return;
        }

        var scale = PdfPageExtension.DisplayScaleFactor();
        if (TryShowCachedPage(page, targetFactor, scale, pos, restoreScroll, fitOnComplete))
            return;

        var version = ++_renderVersion;
        ++_prefetchGeneration;
        var renderFactor = targetFactor;
        var thumbnailRequests = _thumbnailRequests;
        thumbnailRequests?.Pause();

        var pageRender = _pageRenders?.Request(() =>
        {
            BitmapSource image = null;
            try
            {
                image = doc.Render(page, renderFactor, true, scale);
            }
            catch (Exception e)
            {
                // The document may have been disposed while this stale render was
                // queued; skip it instead of crashing.
                Debug.WriteLine(e);
            }

            return image;
        });

        if (pageRender is null)
        {
            thumbnailRequests?.Resume();
            return;
        }

        _ = ApplyPageRenderWhenReady(
            pageRender,
            version,
            page,
            renderFactor,
            factor,
            scale,
            pos,
            restoreScroll,
            fitOnComplete,
            thumbnailRequests);
    }

    private async Task ApplyPageRenderWhenReady(
        Task<BitmapSource> pageRender,
        int version,
        int page,
        double renderFactor,
        double viewFactor,
        double scale,
        Point pos,
        bool restoreScroll,
        bool fitOnComplete,
        PdfThumbnailRequestScheduler thumbnailRequests)
    {
        try
        {
            var image = await pageRender;

            if (!_pdfLoaded || version != _renderVersion || image == null)
            {
                if (_awaitingFirstPage && version == _renderVersion)
                    FinishFirstPage();
                return;
            }

            ApplyRenderedPage(page, renderFactor, image, pos, restoreScroll, fitOnComplete, viewFactor);
            RememberPage(page, renderFactor, scale, image);
        }
        catch (OperationCanceledException)
        {
            // A newer page or zoom replaced this request before it started.
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            if (_awaitingFirstPage && version == _renderVersion)
                FinishFirstPage();
        }
        finally
        {
            thumbnailRequests?.Resume();
        }
    }

    /// <summary>
    /// Shows a page from an earlier render. Returning true means the async path
    /// above is skipped entirely, which is what makes a revisit paint in one frame.
    /// </summary>
    private bool TryShowCachedPage(
        int page,
        double factor,
        double scale,
        Point pos,
        bool restoreScroll,
        bool fitOnComplete)
    {
        if (!TryReadSourceStamp(out var filePath, out var length, out var ticks))
            return false;

        var image = _pageCache.Find(filePath, length, ticks, page, factor, scale, out var usedFactor);
        if (image is null)
            return false;

        // A render queued for a page the user has already left must not replace
        // the bitmap that is about to be shown.
        ++_renderVersion;
        _pageRenders?.CancelPending();
        // v6.1.5: a cached page can be sharper than the current view. Neighbours
        // only need the zoom the user is actually looking at; rendering them at
        // the sharper factor would spend memory the next page never shows.
        ApplyRenderedPage(page, usedFactor, image, pos, restoreScroll, fitOnComplete, factor);
        return true;
    }

    private void ApplyRenderedPage(
        int page,
        double bitmapFactor,
        BitmapSource image,
        Point pos,
        bool restoreScroll,
        bool fitOnComplete,
        double viewFactor)
    {
        // v6.1.7: capture where the user is now, before touching the source.
        // A render takes a moment; if the user scrolled meanwhile, restoring
        // the request-time position would visibly drag the page back.
        var restorePosition = PdfPageDisplay.ScrollToRestore(
            pos, pagePanel.GetScrollPosition());

        // A cached bitmap may be sharper than the view, and the first page is
        // rendered with overscan. Zoom 1:1 would make those pages larger than
        // requested, so the panel zoom is the view factor over the bitmap's.
        var relativeZoom = PdfPageDisplay.ZoomFor(viewFactor, bitmapFactor);

        pagePanel.Source = image;
        pagePanel.MinZoomFactor = _minZoomFactor / bitmapFactor;
        pagePanel.MaxZoomFactor = _maxZoomFactor / bitmapFactor;
        pagePanel.ResetZoom();

        if (Math.Abs(relativeZoom - 1d) > 0.0001d)
            pagePanel.Zoom(relativeZoom, suppressEvent: true);

        _renderedPage = page;
        _renderedFactor = bitmapFactor;

        // The panel's ZoomFactor is relative to this bitmap, so this is the
        // factor the next zoom step must multiply by.
        _viewRenderFactor = bitmapFactor;

        if (restoreScroll)
            pagePanel.SetScrollPosition(restorePosition);

        if (fitOnComplete)
        {
            pagePanel.DoZoomToFit();
        }
        else
        {
            // ResetZoom marks the panel as fit unconditionally. Restore the
            // real state so a window resize refits a fit view and keeps a
            // zoomed one.
            pagePanel.ZoomToFit = PdfPageDisplay.IsAtFit(viewFactor, FitFactorFor(page));
        }

        if (_awaitingFirstPage)
            FinishFirstPage();

        // The page is settled, so there is no gesture left to wait for.
        InvalidateSharpTile(immediate: true);
        SchedulePrefetch(page, viewFactor, PdfPageExtension.DisplayScaleFactor());
    }

    private void RememberPage(int page, double factor, double scale, BitmapSource image)
    {
        if (!TryReadSourceStamp(out var filePath, out var length, out var ticks))
            return;

        _pageCache.Add(filePath, length, ticks, page, factor, scale, image);
    }

    private void OnPageViewChanged(object sender, EventArgs e)
    {
        PositionSharpTile();

        _sharpTileTimer.Stop();
        if (_pdfLoaded && !LowMemoryMode)
            _sharpTileTimer.Start();
    }

    private void OnSharpTileTimerTick(object sender, EventArgs e)
    {
        _sharpTileTimer.Stop();
        EnsureSharpTile();
    }

    private void InvalidateSharpTile(bool immediate = false)
    {
        HideSharpTile();
        _sharpTileTimer.Stop();

        if (!_pdfLoaded || LowMemoryMode)
            return;

        if (immediate)
            EnsureSharpTile();
        else
            _sharpTileTimer.Start();
    }

    /// <summary>
    /// The base page is only rendered below screen density when the pixel cap
    /// engaged. In that case the visible part is rasterized again and painted
    /// over it; every other zoom level keeps the plain page bitmap.
    /// </summary>
    private void EnsureSharpTile()
    {
        if (!_pdfLoaded || LowMemoryMode)
        {
            HideSharpTile();
            return;
        }

        var document = PdfDocumentWrapper;
        var page = CurrentPage;
        var bitmapFactor = _viewRenderFactor;
        var zoom = pagePanel.ZoomFactor;
        if (document is null || page < 0 || page >= TotalPages ||
            bitmapFactor <= 0d || double.IsNaN(bitmapFactor) ||
            zoom <= 0d || double.IsNaN(zoom))
        {
            HideSharpTile();
            return;
        }

        var viewFactor = bitmapFactor * zoom;
        var scale = PdfPageExtension.DisplayScaleFactor();

        Size pageSize;
        try
        {
            var size = document.PdfDocument.PageSizes[page];
            pageSize = new Size(size.Width, size.Height);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            HideSharpTile();
            return;
        }

        if (PdfPageRenderLimit.Ratio(pageSize, viewFactor, scale, 1d) >= 0.999d)
        {
            _lastTilePage = -1;
            _lastTileVisible = Rect.Empty;
            HideSharpTile();
            return;
        }

        var visibleSource = pagePanel.GetVisibleSourceRect();
        if (visibleSource.IsEmpty)
        {
            HideSharpTile();
            return;
        }

        // The source DIP space is the page scaled by the bitmap's factor.
        var visiblePage = PdfSharpTile.VisiblePageRect(visibleSource, bitmapFactor);
        if (visiblePage.IsEmpty)
        {
            HideSharpTile();
            return;
        }

        // The tile covers more of the page in the direction the user has just
        // moved, so a continuing drag stays sharp without a new render.
        var movement = default(Vector);
        if (_lastTilePage == page && !_lastTileVisible.IsEmpty)
        {
            movement = new Vector(
                visiblePage.X - _lastTileVisible.X,
                visiblePage.Y - _lastTileVisible.Y);
        }

        _lastTilePage = page;
        _lastTileVisible = visiblePage;

        if (page == _sharpTilePage &&
            Math.Abs(_sharpTileViewFactor - viewFactor) <= viewFactor * 0.01d &&
            PdfSharpTile.Covers(_sharpTileRegion, visiblePage, 0.5d))
        {
            PositionSharpTile();
            return;
        }

        if (_tileCache.TryFind(
                page, viewFactor, scale, visiblePage, out var cachedTile, out var cachedRegion))
        {
            _sharpTilePage = page;
            _sharpTileViewFactor = viewFactor;
            _sharpTileRegion = cachedRegion;
            sharpTile.Source = cachedTile;
            PositionSharpTile();
            return;
        }

        var region = PdfSharpTile.RegionFor(
            visiblePage, pageSize, SharpTileMargin, SharpTileLeadingMargin, movement);

        if (region.IsEmpty)
        {
            HideSharpTile();
            return;
        }

        var pageRenders = _pageRenders;
        if (pageRenders is null)
            return;

        // A tile is only useful when the view stops moving; a queued page
        // render or prefetch is more important than an outdated tile.
        pageRenders.CancelPrefetch();

        var version = ++_sharpTileVersion;
        var documentGeneration = _documentGeneration;
        var render = pageRenders.Request(
            () =>
            {
                try
                {
                    return document.RenderRegion(page, region, viewFactor, scale);
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e);
                    return null;
                }
            },
            PdfPageRenderPriority.Prefetch);

        _ = ApplySharpTileWhenReady(
            render, version, page, viewFactor, scale, region, documentGeneration);
    }

    private async Task ApplySharpTileWhenReady(
        Task<BitmapSource> render,
        int version,
        int page,
        double viewFactor,
        double scale,
        Rect region,
        int documentGeneration)
    {
        BitmapSource image;
        try
        {
            image = await render;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return;
        }

        if (image is null || version != _sharpTileVersion || !_pdfLoaded ||
            _documentGeneration != documentGeneration || page != CurrentPage)
            return;

        var currentFactor = _viewRenderFactor * pagePanel.ZoomFactor;
        if (currentFactor <= 0d || double.IsNaN(currentFactor) ||
            Math.Abs(currentFactor - viewFactor) > viewFactor * 0.01d)
            return;

        _sharpTilePage = page;
        _sharpTileViewFactor = viewFactor;
        _sharpTileRegion = region;
        sharpTile.Source = image;

        if (!LowMemoryMode)
            _tileCache.Add(page, viewFactor, scale, region, image);

        PositionSharpTile();
    }

    private void PositionSharpTile()
    {
        if (sharpTile.Source is null || _sharpTileRegion.IsEmpty)
            return;

        if (!_pdfLoaded || _sharpTilePage != CurrentPage)
        {
            HideSharpTile();
            return;
        }

        var viewFactor = _viewRenderFactor * pagePanel.ZoomFactor;
        if (viewFactor <= 0d || double.IsNaN(viewFactor) ||
            _sharpTileViewFactor <= 0d ||
            Math.Abs(viewFactor - _sharpTileViewFactor) > _sharpTileViewFactor * 0.01d)
        {
            HideSharpTile();
            return;
        }

        var origin = pagePanel.GetSourceOrigin();
        var viewport = pagePanel.GetViewportSize();

        // Clip to the area inside the scrollbars; the tile may be larger than
        // the viewport because it carries a margin.
        sharpTileLayer.Width = Math.Max(0d, viewport.Width);
        sharpTileLayer.Height = Math.Max(0d, viewport.Height);

        sharpTile.Width = _sharpTileRegion.Width * _sharpTileViewFactor;
        sharpTile.Height = _sharpTileRegion.Height * _sharpTileViewFactor;
        Canvas.SetLeft(sharpTile, origin.X + _sharpTileRegion.X * _sharpTileViewFactor);
        Canvas.SetTop(sharpTile, origin.Y + _sharpTileRegion.Y * _sharpTileViewFactor);

        sharpTile.Visibility = Visibility.Visible;
        sharpTileLayer.Visibility = Visibility.Visible;
    }

    private void HideSharpTile()
    {
        ++_sharpTileVersion;
        _sharpTilePage = -1;
        _sharpTileViewFactor = double.NaN;
        _sharpTileRegion = Rect.Empty;

        sharpTile.Source = null;
        sharpTile.Visibility = Visibility.Collapsed;
        sharpTileLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// After the current page settles, prepares one page on either side for a
    /// side-button or keyboard step. The short delay means a burst of page flips
    /// never starts prefetch at all, and a new foreground page request retires
    /// every prefetch that has not started yet.
    /// </summary>
    private void SchedulePrefetch(int page, double factor, double scale)
    {
        if (LowMemoryMode || !_pdfLoaded || factor <= 0d ||
            double.IsNaN(factor) || TotalPages < 2 ||
            page < 0 || page >= TotalPages)
            return;

        var document = PdfDocumentWrapper;
        var pageRenders = _pageRenders;
        if (document is null || pageRenders is null)
            return;

        // v6.1.8: past the pixel cap a neighbour would be both huge and soft.
        // That render budget belongs to the visible-region tile instead.
        try
        {
            var size = document.PdfDocument.PageSizes[page];
            if (PdfPageRenderLimit.Ratio(
                    new Size(size.Width, size.Height), factor, scale, 1d) < 0.999d)
            {
                // Retire a prefetch queued for the previous zoom level too.
                ++_prefetchGeneration;
                return;
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return;
        }

        var generation = ++_prefetchGeneration;
        var documentGeneration = _documentGeneration;

        Task.Run(async () =>
        {
            await Task.Delay(PrefetchDelayMilliseconds).ConfigureAwait(false);

            if (!IsPrefetchWanted(generation, documentGeneration))
                return;

            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!IsPrefetchWanted(generation, documentGeneration))
                    return;

                pageRenders.CancelPrefetch();
                QueuePagePrefetch(page + 1, factor, scale, document, documentGeneration);
                QueuePagePrefetch(page - 1, factor, scale, document, documentGeneration);
            }));
        });
    }

    private void QueuePagePrefetch(
        int page,
        double factor,
        double scale,
        PdfDocumentWrapper document,
        int documentGeneration)
    {
        var pageRenders = _pageRenders;
        if (LowMemoryMode || !_pdfLoaded || pageRenders is null ||
            _documentGeneration != documentGeneration ||
            !ReferenceEquals(document, PdfDocumentWrapper) ||
            page < 0 || page >= TotalPages)
            return;

        if (!TryReadSourceStamp(out var filePath, out var length, out var ticks))
            return;

        if (_pageCache.Find(filePath, length, ticks, page, factor, scale, out _) is not null)
            return;

        try
        {
            // Do not spend a render on a page the cache would refuse anyway.
            var size = document.PdfDocument.PageSizes[page];
            var pixelScale = scale * factor;
            if (!_pageCache.CanRetain(
                    (long)Math.Ceiling(size.Width * pixelScale),
                    (long)Math.Ceiling(size.Height * pixelScale)))
                return;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            return;
        }

        _ = pageRenders.Request(() =>
        {
            try
            {
                var image = document.Render(page, factor, true, scale);
                if (image is not null)
                {
                    // The entry is keyed by the file stamp captured before the
                    // render, so a file changed during prefetch cannot poison
                    // the next version of the same path.
                    _pageCache.Add(filePath, length, ticks, page, factor, scale, image);
                }

                return image;
            }
            catch (Exception e)
            {
                // A document can be disposed between queueing and rendering;
                // dropping the prefetch is always safe.
                Debug.WriteLine(e);
                return null;
            }
        }, PdfPageRenderPriority.Prefetch);
    }

    private bool IsPrefetchWanted(int generation, int documentGeneration)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke<bool>(() =>
                _pdfLoaded &&
                _documentGeneration == documentGeneration &&
                _prefetchGeneration == generation);
        }

        return _pdfLoaded &&
               _documentGeneration == documentGeneration &&
               _prefetchGeneration == generation;
    }

    /// <summary>
    /// The cache identity is the path plus the file version, exactly like the Office cache: a
    /// replaced or edited PDF never serves pages rendered from its old bytes, and two unrelated
    /// files that happen to have the same size and timestamp cannot share a page.
    /// </summary>
    private bool TryReadSourceStamp(out string filePath, out long length, out long ticks)
    {
        filePath = string.Empty;
        length = 0;
        ticks = 0;

        if (string.IsNullOrEmpty(_sourcePath))
            return false;

        try
        {
            var info = new FileInfo(_sourcePath);
            if (!info.Exists)
                return false;

            filePath = info.FullName;
            length = info.Length;
            ticks = info.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return false;
        }
    }

    private bool RenderFactorIsAlreadySufficient(int page, double factor)
    {
        if (_renderedPage != page || _renderedFactor <= 0d ||
            factor <= 0d || double.IsNaN(factor))
            return false;

        // The bitmap only has to be at least as detailed as the view. Being more
        // detailed is harmless and is exactly what the first-page overscan and
        // zooming back out leave behind; discarding it caused the soft/clear cycle.
        return _renderedFactor >= factor * (1d - RenderFactorTolerance);
    }

    private double FitFactorFor(int page)
    {
        var size = PdfDocumentWrapper.PdfDocument.PageSizes[page];
        return pagePanel.GetZoomToFitFactor(new Size(size.Width, size.Height));
    }

    private void FinishFirstPage()
    {
        _awaitingFirstPage = false;

        if (_thumbnailSchedulerHeldForFirstPage)
        {
            _thumbnailSchedulerHeldForFirstPage = false;
            _thumbnailRequests?.Resume();
        }

        FirstPagePresented?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePageViewWhenSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_pdfLoaded)
            return;

        if (CurrentPage == -1)
            return;

        CurrentPageChanged?.Invoke(this, EventArgs.Empty);

        if (_deferPageRender)
            return;

        if (_initPage)
        {
            _initPage = false;
            ReRenderCurrentPage(fitOnComplete: true);
        }
        else
        {
            ReRenderCurrentPage();
        }
    }

    public static Size GetDesiredControlSizeByFirstPage(string path, string password = null)
    {
        using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            using (var tempHandle = PdfDocument.Load(s, password))
            {
                return ComputeDesiredSize(tempHandle);
            }
        }
    }

    public Size GetDesiredControlSize()
    {
        return ComputeDesiredSize(PdfDocumentWrapper.PdfDocument);
    }

    private static Size ComputeDesiredSize(PdfDocument pdf)
    {
        var size = new Size(0, 0);
        pdf.PageSizes.Take(5).ForEach(p =>
        {
            size.Width = Math.Max(size.Width, p.Width);
            size.Height = Math.Max(size.Height, p.Height);
        });

        if (pdf.PageCount > 1)
            size.Width += /*listThumbnails.ActualWidth*/ 180;

        return new Size(size.Width * 3, size.Height * 3);
    }

    public void LoadPdf(string path, string password = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        LoadPdf(new PdfDocumentWrapper(stream, password), sourcePath: path);
    }

    public void LoadPdf(MemoryStream stream)
    {
        stream.Position = 0;
        LoadPdf(new PdfDocumentWrapper(stream));
    }

    /// <summary>
    /// v3.23.0: applies an already-parsed document (loaded off the UI thread)
    /// without re-reading the file. Must run on the UI thread.
    /// </summary>
    public void LoadPdf(PdfDocumentWrapper wrapper, bool deferPageRender = false, string sourcePath = null)
    {
        ReleaseThumbnailRequests();
        ReleasePageRenders();

        PdfDocumentWrapper = wrapper;
        _pdfLoaded = true;
        _deferPageRender = deferPageRender;
        _awaitingFirstPage = false;
        _renderedPage = -1;
        _renderedFactor = double.NaN;
        _sourcePath = sourcePath;
        _lastTilePage = -1;
        _lastTileVisible = Rect.Empty;
        _tileCache.Clear();
        ++_documentGeneration;
        ++_prefetchGeneration;
        ++_renderVersion;

        _pageRenders = new PdfPageRenderScheduler();
        CreateThumbnailRequests(wrapper, deferPageRender);
        ApplyLoadedDocument(deferPageRender);
    }

    /// <summary>
    /// v6.1.2: starts the first page after the host has applied the document's
    /// final window size. Rendering before that made the page look soft for the
    /// moment between the first frame and the resize-triggered re-render.
    /// </summary>
    public void BeginFirstPageRender()
    {
        if (!_pdfLoaded || CurrentPage < 0 || CurrentPage >= TotalPages)
            return;

        _deferPageRender = false;
        _initPage = true;
        _awaitingFirstPage = true;
        ReRenderCurrentPage(fitOnComplete: true);
    }

    private void ApplyLoadedDocument(bool deferPageRender)
    {
        listThumbnails.Visibility = PdfDocumentWrapper.PdfDocument.PageCount < 2
            ? Visibility.Collapsed
            : Visibility.Visible;

        _deferPageRender = deferPageRender;
        BeginLoadThumbnails();

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            // Replacing the ItemsSource can clear the initial SelectedIndex when
            // the control was created with an empty list.
            if (_pdfLoaded && PageThumbnails.Count > 0 &&
                listThumbnails.SelectedIndex < 0)
                listThumbnails.SelectedIndex = 0;

            UpdateThumbnailDemandRange();
        }));
    }

    private void BeginLoadThumbnails()
    {
        PageThumbnails = new PdfPageIndexList(PdfDocumentWrapper.PdfDocument.PageCount);
        OnPropertyChanged(nameof(PageThumbnails));
    }

    /// <summary>
    /// Called by the thumbnail binding for each realized list item. A cache hit
    /// is returned immediately; a miss joins the viewer's single ordered queue.
    /// </summary>
    internal Task<BitmapSource> RequestThumbnail(PdfDocumentWrapper document, int page)
    {
        var scheduler = _thumbnailRequests;
        if (document is null || scheduler is null ||
            !ReferenceEquals(document, PdfDocumentWrapper))
            return null;

        try
        {
            var cached = PdfPageExtension.FindCachedThumbnail(document, page);
            if (cached is not null)
                return Task.FromResult(cached);
        }
        catch (Exception)
        {
            // A document disposed by a newer preview is no longer a source of
            // thumbnails; the recycled item will be rebound by that preview.
            return null;
        }

        return scheduler.Request(page);
    }

    private void CreateThumbnailRequests(PdfDocumentWrapper document, bool deferPageRender)
    {
        _thumbnailRequests = new PdfThumbnailRequestScheduler(
            document.RenderThumbnail,
            document.PdfDocument.PageCount,
            allowPrefetch: !LowMemoryMode);

        // The first full-size page gets PDFium first. Thumbnail items can be
        // realized while the host is still sizing the window, so hold the queue
        // until FirstPagePresented rather than racing the page the user asked for.
        if (deferPageRender)
        {
            _thumbnailRequests.Pause();
            _thumbnailSchedulerHeldForFirstPage = true;
        }
    }

    private void ReleaseThumbnailRequests()
    {
        _thumbnailDemandTimer?.Stop();
        _thumbnailSchedulerHeldForFirstPage = false;
        _thumbnailFirstVisible = -1;
        _thumbnailLastVisible = -1;

        var requests = _thumbnailRequests;
        _thumbnailRequests = null;
        requests?.Dispose();
    }

    private void ReleasePageRenders()
    {
        var renders = _pageRenders;
        _pageRenders = null;
        renders?.Dispose();
    }

    private void OnThumbnailListLoaded(object sender, RoutedEventArgs e)
    {
        AttachThumbnailScrollViewer();
        UpdateThumbnailDemandRange();
    }

    private void AttachThumbnailScrollViewer()
    {
        if (_thumbnailScrollViewer is not null)
            return;

        _thumbnailScrollViewer = FindVisualChild<ScrollViewer>(listThumbnails);
        if (_thumbnailScrollViewer is not null)
            _thumbnailScrollViewer.ScrollChanged += OnThumbnailScrollChanged;

        _thumbnailItemsPanel ??= FindVisualChild<VirtualizingStackPanel>(listThumbnails);
    }

    private void DetachThumbnailScrollViewer()
    {
        if (_thumbnailScrollViewer is null)
            return;

        _thumbnailScrollViewer.ScrollChanged -= OnThumbnailScrollChanged;
        _thumbnailScrollViewer = null;
        _thumbnailItemsPanel = null;
    }

    private void OnThumbnailScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateThumbnailDemandRange();
    }

    /// <summary>
    /// Translates the virtualized list's viewport into page numbers. The
    /// scheduler can then cancel queued thumbnails that a fast scroll has
    /// already left behind. The timer waits for the scroll to settle before
    /// adding one page on either side.
    /// </summary>
    private void UpdateThumbnailDemandRange()
    {
        var scheduler = _thumbnailRequests;
        var count = PageThumbnails.Count;
        if (!_pdfLoaded || scheduler is null || count <= 0)
            return;

        AttachThumbnailScrollViewer();

        var first = 0;
        var last = count - 1;
        var scroll = _thumbnailScrollViewer;

        if (scroll is not null && scroll.CanContentScroll && scroll.ViewportHeight > 0d)
        {
            first = (int)Math.Floor(scroll.VerticalOffset);
            last = (int)Math.Ceiling(scroll.VerticalOffset + scroll.ViewportHeight) - 1;
        }
        else if (scroll is not null && scroll.ExtentHeight > 0d &&
                 scroll.ViewportHeight > 0d)
        {
            var pageHeight = scroll.ExtentHeight / count;
            if (pageHeight > 0d)
            {
                first = (int)Math.Floor(scroll.VerticalOffset / pageHeight);
                last = (int)Math.Ceiling(
                    (scroll.VerticalOffset + scroll.ViewportHeight) / pageHeight) - 1;
            }
        }

        first = Math.Clamp(first, 0, count - 1);
        last = Math.Clamp(last, first, count - 1);
        _thumbnailFirstVisible = first;
        _thumbnailLastVisible = last;

        scheduler.SetVisibleRange(first, last, GetRealizedThumbnailPages());

        _thumbnailDemandTimer.Stop();
        if (!LowMemoryMode)
            _thumbnailDemandTimer.Start();
    }

    private void OnThumbnailDemandTimerTick(object sender, EventArgs e)
    {
        _thumbnailDemandTimer.Stop();

        var scheduler = _thumbnailRequests;
        if (!_pdfLoaded || LowMemoryMode || scheduler is null ||
            _thumbnailFirstVisible < 0)
            return;

        var previous = _thumbnailFirstVisible - 1;
        if (previous >= 0)
            _ = scheduler.Request(previous, ThumbnailRequestKind.Prefetch);

        var next = _thumbnailLastVisible + 1;
        if (next < PageThumbnails.Count)
            _ = scheduler.Request(next, ThumbnailRequestKind.Prefetch);
    }

    /// <summary>
    /// A virtualized container survives briefly outside the viewport. Its
    /// binding will not run again by itself, so its queued page must not be
    /// cancelled until WPF has actually recycled the container for another page.
    /// </summary>
    private HashSet<int> GetRealizedThumbnailPages()
    {
        _thumbnailItemsPanel ??= FindVisualChild<VirtualizingStackPanel>(listThumbnails);
        if (_thumbnailItemsPanel is null)
            return null;

        var pages = new HashSet<int>();
        foreach (UIElement child in _thumbnailItemsPanel.Children)
        {
            if (child is ListBoxItem { DataContext: int page })
                pages.Add(page);
        }

        return pages;
    }

    private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
