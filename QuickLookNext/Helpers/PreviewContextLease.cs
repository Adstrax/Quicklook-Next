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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Plugin;
using System;
using System.ComponentModel;
using System.Windows;

namespace QuickLookNext.Helpers;

/// <summary>
/// v6.0.0: gives every preview its own <see cref="ContextObject"/> and owns the events that connect
/// it to the viewer window.
/// <para>
/// A plugin can finish decoding an image, closing a video or measuring a document after the user has
/// already switched files. Those continuations only hold the context they were started with. Reusing
/// one context meant a late write could replace the new preview's content or resize its window. When
/// the preview changes, this lease detaches the old context first; later writes have nowhere to go.
/// </para>
/// </summary>
internal sealed class PreviewContextLease
{
    private readonly object _source;
    private readonly PropertyChangedEventHandler _propertyChanged;
    private readonly Action<Size> _resizeRequested;

    private ContextObject _current;
    private PropertyChangedEventHandler _currentPropertyChanged;
    private Action<Size> _currentResizeRequested;
    private Themes _theme = Themes.None;

    internal PreviewContextLease(object source, PropertyChangedEventHandler propertyChanged,
        Action<Size> resizeRequested)
    {
        _source = source;
        _propertyChanged = propertyChanged;
        _resizeRequested = resizeRequested;
    }

    internal ContextObject Current => _current;

    /// <summary>Creates the first context. It is not reset so the window keeps its normal defaults.</summary>
    internal ContextObject Initialize(Themes theme)
    {
        if (_current != null)
            throw new InvalidOperationException("The preview context lease is already initialized.");

        _theme = theme;
        var context = new ContextObject
        {
            Source = _source,
            Theme = theme,
            IsLowMemoryMode = LowMemoryMode.WillReleaseMemory,
        };
        Attach(context);
        return context;
    }

    /// <summary>
    /// Replaces the context with a clean one for the next preview. The previous theme is carried
    /// over because <see cref="ContextObject.Reset"/> has always preserved it.
    /// </summary>
    internal ContextObject Replace(Themes fallbackTheme)
    {
        var theme = _current?.Theme ?? _theme;
        Detach();

        _theme = theme;
        var context = new ContextObject
        {
            Source = _source,
            Theme = theme,
            IsLowMemoryMode = LowMemoryMode.WillReleaseMemory,
        };
        context.Reset();
        Attach(context);
        return context;
    }

    /// <summary>
    /// Stops the current context from reaching the window. The context object itself stays intact
    /// for plugins that still hold it and may finish in the background.
    /// </summary>
    internal void Detach()
    {
        if (_current == null)
            return;

        _theme = _current.Theme;
        _current.PropertyChanged -= _currentPropertyChanged;
        _current.ResizeRequested -= _currentResizeRequested;
        _currentPropertyChanged = null;
        _currentResizeRequested = null;
        _current = null;
    }

    private void Attach(ContextObject context)
    {
        // The captured context check also covers an event delegate that was already being invoked
        // while the lease moved on: the old invocation must not reach the new preview.
        PropertyChangedEventHandler propertyChanged = (sender, e) =>
        {
            if (ReferenceEquals(_current, sender) && ReferenceEquals(_current, context))
                _propertyChanged(sender, e);
        };
        Action<Size> resizeRequested = size =>
        {
            if (ReferenceEquals(_current, context))
                _resizeRequested(size);
        };

        _current = context;
        _currentPropertyChanged = propertyChanged;
        _currentResizeRequested = resizeRequested;

        context.PropertyChanged += propertyChanged;
        context.ResizeRequested += resizeRequested;
    }
}
