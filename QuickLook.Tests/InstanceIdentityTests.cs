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

using QuickLookNext;

namespace QuickLook.Tests;

/// <summary>
/// v6.1.2: a development build can set QL_INSTANCE_SUFFIX so its measurement
/// instance never forwards to the user's installed tray app. The historical
/// names must stay byte-for-byte identical when the variable is absent, and the
/// suffix must be safe to use in both a mutex and a pipe name.
/// </summary>
internal class InstanceIdentityTests
{
    public void DefaultNamesAreUnchanged()
    {
        Assert.Equal(string.Empty, InstanceIdentity.NormalizeSuffix(null), "no suffix");
        Assert.Equal("QuickLookNext.App.Mutex", InstanceIdentity.MutexNameFor(string.Empty), "mutex");
        Assert.Equal(
            "QuickLookNext.App.Pipe.S-1-5-21-1",
            InstanceIdentity.PipeNameFor("S-1-5-21-1", string.Empty),
            "pipe");
    }

    public void BlankSuffixFallsBackToTheDefault()
    {
        Assert.Equal(string.Empty, InstanceIdentity.NormalizeSuffix(""), "empty value");
        Assert.Equal(string.Empty, InstanceIdentity.NormalizeSuffix("   "), "whitespace only");
        Assert.Equal(string.Empty, InstanceIdentity.NormalizeSuffix("***"), "no usable characters");
        Assert.Equal(string.Empty, InstanceIdentity.NormalizeSuffix("  /\\:  "), "separators only");
    }

    public void SafeCharactersAreKeptAndTrimmed()
    {
        Assert.Equal(".measure-preview", InstanceIdentity.NormalizeSuffix(" measure-preview "), "trimmed");
        Assert.Equal(".bench_1.2", InstanceIdentity.NormalizeSuffix("bench_1.2"), "letters, digits, dot");
    }

    public void UnsafeCharactersAreDropped()
    {
        Assert.Equal(
            ".measurepreview",
            InstanceIdentity.NormalizeSuffix(@" measure\preview/:*?""<>| "),
            "path and name separators");
    }

    public void SuffixIsBounded()
    {
        var suffix = InstanceIdentity.NormalizeSuffix(new string('a', 100));

        Assert.Equal(32, suffix.Length, "dot plus 31 characters");
        Assert.Equal('.', suffix[0], "leading dot");
    }

    public void SuffixIsAppliedToBothNames()
    {
        var suffix = InstanceIdentity.NormalizeSuffix("bench");
        var mutex = InstanceIdentity.MutexNameFor(suffix);
        var pipe = InstanceIdentity.PipeNameFor("S-1-5-21-1", suffix);

        Assert.Equal("QuickLookNext.App.Mutex.bench", mutex, "mutex");
        Assert.Equal("QuickLookNext.App.Pipe.S-1-5-21-1.bench", pipe, "pipe");
        Assert.False(mutex == InstanceIdentity.MutexNameFor(string.Empty), "mutex is isolated");
        Assert.False(
            pipe == InstanceIdentity.PipeNameFor("S-1-5-21-1", string.Empty),
            "pipe is isolated");
    }
}
