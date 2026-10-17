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

using QuickLook.Common.Plugin;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace QuickLook.Plugin.ImageViewer.AnimatedImage;

internal abstract class AnimationProvider(Uri path, MetaProvider meta, ContextObject contextObject) : IDisposable
{
    public Uri Path { get; } = path;

    public MetaProvider Meta { get; } = meta;

    public ContextObject ContextObject { get; } = contextObject;

    public Int32AnimationUsingKeyFrames Animator { get; protected set; }

    public abstract void Dispose();

    /// <summary>
    /// Stops provider-owned animation loops without releasing the last frame. The host calls
    /// this when another preview starts; <see cref="Dispose"/> still runs after the swap.
    /// </summary>
    public virtual void Pause()
    {
    }

    public abstract Task<BitmapSource> GetThumbnail(Size renderSize);

    public virtual Task<BitmapSource> GetThumbnail(Size renderSize, CancellationToken cancellationToken)
        => GetThumbnail(renderSize);

    public abstract Task<BitmapSource> GetRenderedFrame(int index);

    public virtual Task<BitmapSource> GetRenderedFrame(int index, CancellationToken cancellationToken)
        => GetRenderedFrame(index);
}
