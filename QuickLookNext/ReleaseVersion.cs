// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;

namespace QuickLookNext;

/// <summary>
/// Orders release tags using the SemVer rules the updater needs: numeric core
/// components, optional prerelease identifiers, and a stable release ahead of a
/// prerelease with the same core version.
/// </summary>
internal sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly int[] _core;
    private readonly string[] _prerelease;

    private ReleaseVersion(int[] core, string[] prerelease, string displayVersion)
    {
        _core = core;
        _prerelease = prerelease;
        DisplayVersion = displayVersion;
    }

    internal string DisplayVersion { get; }

    internal bool IsPrerelease => _prerelease.Length > 0;

    internal static bool TryParse(string value, out ReleaseVersion version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (text.Length > 1 && text[0] is 'v' or 'V')
            text = text[1..];

        var metadata = text.IndexOf('+');
        if (metadata >= 0)
            text = text[..metadata];
        if (text.Length == 0)
            return false;

        var separator = text.IndexOf('-');
        var coreText = separator >= 0 ? text[..separator] : text;
        var prereleaseText = separator >= 0 ? text[(separator + 1)..] : string.Empty;

        var coreParts = coreText.Split('.');
        if (coreParts.Length is < 2 or > 4)
            return false;

        var core = new int[coreParts.Length];
        for (var i = 0; i < coreParts.Length; i++)
        {
            if (!int.TryParse(coreParts[i], out core[i]) || core[i] < 0)
                return false;
        }

        var prerelease = Array.Empty<string>();
        if (separator >= 0)
        {
            if (prereleaseText.Length == 0)
                return false;

            prerelease = prereleaseText.Split('.');
            if (Array.Exists(prerelease, part => part.Length == 0))
                return false;
        }

        version = new ReleaseVersion(core, prerelease, text);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        if (other is null)
            return 1;

        var coreLength = Math.Max(_core.Length, other._core.Length);
        for (var i = 0; i < coreLength; i++)
        {
            var left = i < _core.Length ? _core[i] : 0;
            var right = i < other._core.Length ? other._core[i] : 0;
            var comparison = left.CompareTo(right);
            if (comparison != 0)
                return comparison;
        }

        if (_prerelease.Length == 0)
            return other._prerelease.Length == 0 ? 0 : 1;
        if (other._prerelease.Length == 0)
            return -1;

        var prereleaseLength = Math.Min(_prerelease.Length, other._prerelease.Length);
        for (var i = 0; i < prereleaseLength; i++)
        {
            var comparison = ComparePrereleaseIdentifier(_prerelease[i], other._prerelease[i]);
            if (comparison != 0)
                return comparison;
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = long.TryParse(left, out var leftNumber);
        var rightNumeric = long.TryParse(right, out var rightNumber);

        if (leftNumeric && rightNumeric)
            return leftNumber.CompareTo(rightNumber);
        if (leftNumeric)
            return -1;
        if (rightNumeric)
            return 1;

        return string.Compare(left, right, StringComparison.Ordinal);
    }
}
