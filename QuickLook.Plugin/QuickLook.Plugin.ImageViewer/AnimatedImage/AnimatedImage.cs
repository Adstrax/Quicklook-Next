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

using QuickLook.Common.ExtensionMethods;
using QuickLook.Common.Plugin;
using QuickLook.Plugin.ImageViewer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace QuickLook.Plugin.ImageViewer.AnimatedImage;

public class AnimatedImage : Image, IDisposable
{
    // List<Pair<formats, type>>
    public static List<KeyValuePair<string[], Type>> Providers = [];

    private AnimationProvider _animation;
    private bool _disposing;
    private int _loadVersion;
    private CancellationTokenSource _loadCancellation;

    public void Dispose()
    {
        _disposing = true;
        _loadVersion++;
        CancelPendingLoad();

        BeginAnimation(AnimationFrameIndexProperty, null);
        Source = null;

        _animation?.Dispose();
        _animation = null;
    }

    public event EventHandler ImageLoaded;

    public event EventHandler DoZoomToFit;

    /// <summary>
    /// v6.1.0: the loaded bitmap changed its own pixel size (the detail reload). The panel
    /// uses this to keep the same visible size and scroll position while the zoom factor is
    /// converted from the old bitmap's coordinate space to the new one.
    /// </summary>
    public event EventHandler<SourceSizeChangedEventArgs> SourceSizeChanged;

    private static AnimationProvider InitAnimationProvider(Uri path, MetaProvider meta, ContextObject contextObject)
    {
        var ext = Path.GetExtension(path.LocalPath).ToLower();
        var type = Providers.First(p => p.Key.Contains(ext) || p.Key.Contains("*")).Value;

        var provider = type.CreateInstance<AnimationProvider>(path, meta, contextObject);

        return provider;
    }

    public static readonly DependencyProperty AnimationFrameIndexProperty =
        DependencyProperty.Register(nameof(AnimationFrameIndex), typeof(int), typeof(AnimatedImage),
            new UIPropertyMetadata(-1, AnimationFrameIndexChanged));

    public static readonly DependencyProperty AnimationUriProperty =
        DependencyProperty.Register(nameof(AnimationUri), typeof(Uri), typeof(AnimatedImage),
            new UIPropertyMetadata(null, AnimationUriChanged));

    public static readonly DependencyProperty MetaProperty =
        DependencyProperty.Register(nameof(Meta), typeof(MetaProvider), typeof(AnimatedImage));

    public static readonly DependencyProperty ContextObjectProperty =
        DependencyProperty.Register(nameof(ContextObject), typeof(ContextObject), typeof(AnimatedImage));

    public int AnimationFrameIndex
    {
        get => (int)GetValue(AnimationFrameIndexProperty);
        set => SetValue(AnimationFrameIndexProperty, value);
    }

    public Uri AnimationUri
    {
        get => (Uri)GetValue(AnimationUriProperty);
        set => SetValue(AnimationUriProperty, value);
    }

    public MetaProvider Meta
    {
        private get => (MetaProvider)GetValue(MetaProperty);
        set => SetValue(MetaProperty, value);
    }

    public ContextObject ContextObject
    {
        private get => (ContextObject)GetValue(ContextObjectProperty);
        set => SetValue(ContextObjectProperty, value);
    }

    private static void AnimationUriChanged(DependencyObject obj, DependencyPropertyChangedEventArgs ev)
    {
        if (obj is not AnimatedImage instance)
            return;

        instance._animation = InitAnimationProvider((Uri)ev.NewValue, instance.Meta, instance.ContextObject);
        ShowThumbnailAndStartAnimation(instance);
    }

    /// <summary>
    /// v5.6.6: re-decodes the frame at a larger size without touching the view. Used when the user
    /// zooms past the detail the first decode had. Unlike the initial load this must not refit the
    /// zoom or re-fire ImageLoaded - the placeholder is always scaled to the same capped target
    /// size, so the geometry is unchanged and only the detail improves.
    /// </summary>
    public void ReloadAt(Size renderSize) => ShowThumbnailAndStartAnimation(this, renderSize, keepView: true);

    /// <summary>
    /// v6.0.0: another preview is opening. Reject the result of work already in flight and stop
    /// frame animation, but keep the last decoded source visible until the host swaps content.
    /// </summary>
    public void Quiesce()
    {
        if (_disposing)
            return;

        _loadVersion++;
        CancelPendingLoad();
        BeginAnimation(AnimationFrameIndexProperty, null);
        _animation?.Pause();
    }

    private static void ShowThumbnailAndStartAnimation(AnimatedImage instance, Size? renderSize = null,
        bool keepView = false)
    {
        instance.CancelPendingLoad();
        var cancellation = new CancellationTokenSource();
        instance._loadCancellation = cancellation;
        var version = ++instance._loadVersion;

        // The first request comes from the host in DIP. A detail request already carries device
        // pixels, so only the initial size is converted here.
        var requestedSize = renderSize ??
                            ImageHelper.ToDevicePixels(instance.ContextObject.PreferredSize);
        var task = instance._animation.GetThumbnail(requestedSize, cancellation.Token);
        if (task == null) return;

        task.ContinueWith(completed => instance.Dispatcher.Invoke(() =>
        {
            instance.CompleteLoad(cancellation);

            // v6.1.0: a newer decode (another file, or a newer detail request) supersedes this
            // one. The native decoder cannot always be cancelled, but its result must not
            // replace the picture the user is now looking at.
            if (instance._disposing || version != instance._loadVersion)
                return;

            if (completed.IsCanceled || completed.IsFaulted)
                return;

            // v5.6.14: a null thumbnail must not wipe a picture that is already on screen. Formats without
            // an embedded thumbnail (WebP, and anything else that only has full frames) return null here,
            // and the detail reload runs *after* the first frame was rendered - so assigning it blanked the
            // preview: the image appeared, then became the window's own background. A static picture never
            // triggers another frame, so nothing brought it back.
            if (completed.Result != null)
                SetSource(instance, completed.Result);

            if (completed.Result != null && !keepView)
            {
                instance.DoZoomToFit?.Invoke(instance, EventArgs.Empty);
                instance.ImageLoaded?.Invoke(instance, EventArgs.Empty);
            }

            // A detail reload keeps the running frame animation rather than restarting it.
            if (!keepView && instance._animation?.Animator is { } animator)
                instance.BeginAnimation(AnimationFrameIndexProperty, animator);
        }));
        task.Start();
    }

    private static void AnimationFrameIndexChanged(DependencyObject obj, DependencyPropertyChangedEventArgs ev)
    {
        if (obj is not AnimatedImage instance)
            return;

        if (instance._disposing)
            return;

        instance.CancelPendingLoad();
        var cancellation = new CancellationTokenSource();
        instance._loadCancellation = cancellation;
        var version = ++instance._loadVersion;
        var task = instance._animation.GetRenderedFrame((int)ev.NewValue, cancellation.Token);

        task.ContinueWith(completed => instance.Dispatcher.Invoke(() =>
        {
            instance.CompleteLoad(cancellation);

            if (instance._disposing || version != instance._loadVersion)
                return;

            if (completed.IsCanceled || completed.IsFaulted)
                return;

            var firstLoad = instance.Source == null;

            if (completed.Result != null)
                SetSource(instance, completed.Result);

            if (firstLoad)
            {
                instance.DoZoomToFit?.Invoke(instance, EventArgs.Empty);
                instance.ImageLoaded?.Invoke(instance, EventArgs.Empty);
            }
        }));
        task.Start();
    }

    private void CancelPendingLoad()
    {
        var cancellation = _loadCancellation;
        _loadCancellation = null;
        if (cancellation == null)
            return;

        cancellation.Cancel();
    }

    private void CompleteLoad(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_loadCancellation, cancellation))
            _loadCancellation = null;

        cancellation.Dispose();
    }

    private static void SetSource(AnimatedImage instance, System.Windows.Media.ImageSource source)
    {
        var previous = instance.Source;
        var oldSize = previous == null
            ? Size.Empty
            : new Size(previous.Width, previous.Height);

        instance.Source = source;

        if (source == null || oldSize.IsEmpty)
            return;

        var newSize = new Size(source.Width, source.Height);

        if (Math.Abs(oldSize.Width - newSize.Width) < 0.5 &&
            Math.Abs(oldSize.Height - newSize.Height) < 0.5)
        {
            return;
        }

        instance.SourceSizeChanged?.Invoke(
            instance, new SourceSizeChangedEventArgs(oldSize, newSize));
    }
}

public sealed class SourceSizeChangedEventArgs : EventArgs
{
    public SourceSizeChangedEventArgs(Size oldSize, Size newSize)
    {
        OldSize = oldSize;
        NewSize = newSize;
    }

    public Size OldSize { get; }
    public Size NewSize { get; }
}
