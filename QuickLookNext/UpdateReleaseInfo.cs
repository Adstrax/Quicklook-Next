// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using Newtonsoft.Json.Linq;
using System;
using System.Globalization;

namespace QuickLookNext;

/// <summary>
/// The release details the update prompt needs. GitHub's release API already
/// includes the notes, so opening the release page is no longer required just to
/// find out what changed.
/// </summary>
internal sealed record UpdateReleaseInfo(
    string Version,
    string Title,
    string Notes,
    string ReleaseNotesUrl,
    DateTimeOffset? PublishedAt)
{
    private const int MaximumNotesLength = 100_000;

    internal static UpdateReleaseInfo FromRelease(JObject release, string fallbackVersion = null)
    {
        return Create(
            (string)release?["tag_name"],
            (string)release?["name"],
            (string)release?["body"],
            (string)release?["html_url"],
            (string)release?["published_at"],
            fallbackVersion);
    }

    internal static UpdateReleaseInfo Create(
        string version,
        string title,
        string notes,
        string releaseNotesUrl,
        string publishedText,
        string fallbackVersion = null)
    {
        version = string.IsNullOrWhiteSpace(version) ? fallbackVersion ?? "?" : version;
        title = string.IsNullOrWhiteSpace(title) ? $"QuickLook-Next {version}" : title.Trim();
        notes ??= string.Empty;
        if (notes.Length > MaximumNotesLength)
            notes = notes[..MaximumNotesLength] + Environment.NewLine + Environment.NewLine + "…";

        DateTimeOffset? published = null;
        if (DateTimeOffset.TryParse(
                publishedText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            published = parsed;
        }

        return new UpdateReleaseInfo(version, title, notes.Trim(), releaseNotesUrl, published);
    }
}
