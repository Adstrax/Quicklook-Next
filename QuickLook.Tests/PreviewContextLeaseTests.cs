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
using QuickLookNext.Helpers;
using System.Windows;

namespace QuickLook.Tests;

/// <summary>
/// v6.0.0: a preview that has been replaced cannot push late content or size requests into the
/// window that now shows another file.
/// </summary>
internal class PreviewContextLeaseTests
{
    public void AnOldPreviewCannotWriteIntoTheNewOne()
    {
        var propertyChanges = 0;
        var resizeRequests = 0;
        var lease = new PreviewContextLease(this,
            (_, _) => propertyChanges++,
            _ => resizeRequests++);

        var oldContext = lease.Initialize(Themes.None);
        var newContext = lease.Replace(Themes.None);

        oldContext.ViewerContent = new object();
        oldContext.IsBusy = true;
        oldContext.Title = "late image";
        var resized = oldContext.ApplyPreferredSizeNow();

        Assert.False(resized, "the replaced context has no window listener");
        Assert.Equal(0, propertyChanges, "late content and busy-state writes are ignored");
        Assert.Equal(0, resizeRequests, "a late size request cannot resize the new preview");

        newContext.ViewerContent = new object();
        Assert.Equal(1, propertyChanges, "the current context still reaches the window");
    }

    public void ANewPreviewStartsWithACleanContext()
    {
        var lease = new PreviewContextLease(this, (_, _) => { }, _ => { });
        var oldContext = lease.Initialize(Themes.None);
        oldContext.ViewerContent = new object();
        oldContext.PendingViewerContent = new object();
        oldContext.IsBusy = true;
        oldContext.PreferredSize = new Size(1920, 1080);

        var newContext = lease.Replace(Themes.Dark);

        Assert.Null(newContext.ViewerContent, "old content is not carried into the new preview");
        Assert.Null(newContext.PendingViewerContent, "old pending content is cleared");
        Assert.False(newContext.IsBusy, "the new preview starts with a clean busy state");
        Assert.Equal(0d, newContext.PreferredSize.Width, "the old size cannot leak across");
        Assert.Equal(0d, newContext.PreferredSize.Height, "both dimensions are cleared");
    }

    public void TheThemeSurvivesTheContextChange()
    {
        var lease = new PreviewContextLease(this, (_, _) => { }, _ => { });
        var first = lease.Initialize(Themes.Light);
        first.Theme = Themes.Dark;

        var second = lease.Replace(Themes.None);

        Assert.Equal(Themes.Dark, second.Theme, "a plugin without a theme keeps the chosen one");
    }
}
