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
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLookNext.Helpers;

/// <summary>
/// Loads the release history only when the user opens it. Nothing is cached on
/// disk, and the response is deliberately capped so a malformed release feed
/// cannot keep an unbounded amount of text in memory.
/// </summary>
internal static class ReleaseHistoryService
{
    internal const int MaximumReleases = 20;

    private const string ReleasesApi =
        "https://api.github.com/repos/Adstrax/QuickLook-Next/releases?per_page=30";

    internal static async Task<IReadOnlyList<UpdateReleaseInfo>> LoadAsync(
        CancellationToken cancellationToken)
    {
        using var client = Updater.CreateHttpClient(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        return ParseReleasesJson(json);
    }

    internal static IReadOnlyList<UpdateReleaseInfo> ParseReleasesJson(string json)
    {
        var array = JArray.Parse(json);
        var releases = new List<UpdateReleaseInfo>(MaximumReleases);
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in array)
        {
            if (token is not JObject release ||
                (bool?)release["draft"] == true ||
                (bool?)release["prerelease"] == true)
            {
                continue;
            }

            var tag = (string)release["tag_name"];
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var info = UpdateReleaseInfo.FromRelease(release);
            if (string.IsNullOrWhiteSpace(info.Version) ||
                info.Version == "?" ||
                !versions.Add(info.Version))
            {
                continue;
            }

            if (string.IsNullOrEmpty(info.ReleaseNotesUrl))
            {
                info = info with
                {
                    ReleaseNotesUrl =
                        $"https://github.com/Adstrax/QuickLook-Next/releases/tag/{Uri.EscapeDataString(tag)}",
                };
            }

            releases.Add(info);
            if (releases.Count >= MaximumReleases)
                break;
        }

        return releases;
    }
}
