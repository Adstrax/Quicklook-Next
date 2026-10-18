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

using QuickLook.Plugin.Shared;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Tests;

/// <summary>
/// v6.1.2: the WebView2 environment is one single-flight operation. Concurrent
/// previews must join it, while a failure or an idle browser shutdown must not
/// leave a dead environment cached for the next preview.
/// </summary>
internal class SingleFlightCacheTests
{
    public void ConcurrentCallersJoinTheSameOperation()
    {
        var calls = 0;
        var source = new TaskCompletionSource<object>();
        var cache = new SingleFlightCache<object>(() =>
        {
            Interlocked.Increment(ref calls);
            return source.Task;
        });

        var first = cache.GetOrStart();
        var second = cache.GetOrStart();

        Assert.Equal(1, calls, "the factory runs once");
        Assert.True(ReferenceEquals(first, second), "both callers await the same task");

        source.SetResult(new object());
        Assert.True(ReferenceEquals(cache.GetOrStart(), first), "a completed operation stays cached");
    }

    public void ForgetStartsAFreshOperation()
    {
        var calls = 0;
        var cache = new SingleFlightCache<object>(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<object>(new());
        });

        var first = cache.GetOrStart();
        cache.Forget();
        var second = cache.GetOrStart();

        Assert.Equal(2, calls, "forgetting discards the previous operation");
        Assert.False(ReferenceEquals(first, second), "the next caller gets a fresh task");
    }

    public void AFaultedOperationIsNotRetriedByTheCache()
    {
        var calls = 0;
        var cache = new SingleFlightCache<object>(() =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException<object>(new InvalidOperationException("boom"));
        });

        var first = cache.GetOrStart();
        var second = cache.GetOrStart();

        Assert.Equal(2, calls, "a faulted task is replaced on the next call");
        Assert.False(ReferenceEquals(first, second), "the failed task is not handed out again");
    }
}
