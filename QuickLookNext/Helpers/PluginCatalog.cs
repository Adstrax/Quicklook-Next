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

using QuickLook.Common.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace QuickLookNext.Helpers;

/// <summary>
/// v5.6.0: one entry of the plugin catalogue - a third-party plugin that its
/// own author publishes as a .qlplugin. Nothing is mirrored by this app: the
/// catalogue only points at the author's release asset, and carries the size
/// and SHA-256 of that exact file so the download can be verified.
/// </summary>
internal sealed class PluginCatalogEntry
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string RepoUrl { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Asset { get; init; } = string.Empty;
    public long Size { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    /// <summary>"753 KB" / "22.3 MB" - what the row shows next to the version.</summary>
    public string SizeText => Size >= 1024 * 1024
        ? string.Format(CultureInfo.InvariantCulture, "{0:0.#} MB", Size / 1024d / 1024d)
        : string.Format(CultureInfo.InvariantCulture, "{0:0} KB", Size / 1024d);
}

/// <summary>
/// v5.6.0: reads the plugin catalogue that <c>Scripts/build-plugin-index.ps1</c>
/// generates from the upstream wiki plus the GitHub API.
/// <para>
/// Every entry is validated before it is offered: the download URL has to be an
/// https link on a GitHub host, the hash has to be a full SHA-256, and the size
/// has to be a plausible plugin package. An entry that fails any of those is
/// dropped rather than shown, so the panel can never offer an install it cannot
/// verify.
/// </para>
/// </summary>
internal static class PluginCatalog
{
    /// <summary>
    /// The catalogue lives in this repository, so refreshing it is a commit and
    /// not a release. raw.githubusercontent.com is on the updater's host
    /// allowlist, which is what <see cref="Updater.IsTrustedDownloadUrl"/> checks.
    /// </summary>
    internal const string IndexUrl =
        "https://raw.githubusercontent.com/Adstrax/Quicklook-Next/opt/3.30-hardening/plugins/index.json";

    /// <summary>
    /// A .qlplugin is a zip of managed assemblies. The largest real one in the
    /// catalogue is 47 MB; anything past this is not a plugin package.
    /// </summary>
    internal const long MaxPluginBytes = 200L * 1024 * 1024;

    /// <summary>Last successful catalogue, kept so the list still opens offline.</summary>
    private static string CachePath =>
        Path.Combine(SettingHelper.LocalDataPath, "plugin-index.json");

    private static IReadOnlyList<PluginCatalogEntry> _cached;

    /// <summary>
    /// Parses and validates a catalogue document. <paramref name="rejected"/>
    /// counts the entries that were dropped, so the panel can say so instead of
    /// silently showing a shorter list than the file contains.
    /// </summary>
    internal static IReadOnlyList<PluginCatalogEntry> Parse(string json, out int rejected)
    {
        var result = new List<PluginCatalogEntry>();
        rejected = 0;

        if (string.IsNullOrWhiteSpace(json))
            return result;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return result;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("plugins", out var plugins) ||
                plugins.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in plugins.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    rejected++;
                    continue;
                }

                var entry = new PluginCatalogEntry
                {
                    Id = ReadString(item, "id"),
                    Name = ReadString(item, "name"),
                    Publisher = ReadString(item, "publisher"),
                    Description = ReadString(item, "description"),
                    RepoUrl = ReadString(item, "repoUrl"),
                    Version = ReadString(item, "version"),
                    Asset = ReadString(item, "asset"),
                    Size = ReadLong(item, "size"),
                    Sha256 = ReadString(item, "sha256"),
                    Url = ReadString(item, "url"),
                };

                if (IsUsable(entry))
                    result.Add(entry);
                else
                    rejected++;
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the catalogue, preferring the live copy and falling back to the
    /// last one that downloaded successfully. Never throws: a network problem
    /// degrades to "no list" plus an error string.
    /// </summary>
    internal static IReadOnlyList<PluginCatalogEntry> Load(out string error, bool forceRefresh = false)
    {
        error = string.Empty;

        if (!forceRefresh && _cached is not null)
            return _cached;

        if (!forceRefresh)
        {
            var cached = TryReadCache(out var cachedError);
            if (cached.Count > 0)
            {
                _cached = cached;
                error = cachedError;
                return cached;
            }
        }

        try
        {
            using var client = Updater.CreateHttpClient(TimeSpan.FromSeconds(20));
            var json = client.GetStringAsync(IndexUrl).GetAwaiter().GetResult();
            var entries = Parse(json, out var rejected);

            if (entries.Count == 0)
            {
                error = "The plugin list could not be read.";
                return entries;
            }

            _cached = entries;
            TryWriteCache(json);

            if (rejected > 0)
                error = $"{rejected} entr{(rejected == 1 ? "y was" : "ies were")} ignored (they failed validation).";

            return entries;
        }
        catch (Exception e)
        {
            var cached = TryReadCache(out _);
            if (cached.Count > 0)
            {
                _cached = cached;
                error = "Could not reach the plugin list; showing the last one that was downloaded.";
                return cached;
            }

            error = "Could not reach the plugin list: " + e.Message;
            return [];
        }
    }

    /// <summary>Test seam: drops the memoised catalogue.</summary>
    internal static void ResetCache() => _cached = null;

    /// <summary>
    /// v5.6.0: test hook. Fetches the published catalogue and describes what
    /// came back, so the smoke test can prove the live list is reachable and
    /// parses without anyone having to click through the panel.
    /// </summary>
    internal static string Diagnose()
    {
        var entries = Load(out var error, forceRefresh: true);

        var lines = new List<string>
        {
            $"source={IndexUrl}",
            $"count={entries.Count}",
            $"error={error}",
            $"totalMB={entries.Sum(e => e.Size) / 1024d / 1024d:0.0}",
            $"publishers={entries.Select(e => e.Publisher).Distinct(StringComparer.OrdinalIgnoreCase).Count()}",
        };

        foreach (var entry in entries.Take(5))
            lines.Add($"entry={entry.Id}@{entry.Version} {entry.SizeText} sha={entry.Sha256[..8]}");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// v5.6.0: is the catalogue's version newer than what is installed? Both
    /// sides are loose ("v1.0.4", "6", "1.3.6.0"), so only the leading dotted
    /// numeric part is compared; when either side has no such part the answer is
    /// false, which keeps the panel from offering an update it cannot justify.
    /// </summary>
    internal static bool IsNewerThan(string candidate, string installed)
    {
        var a = ReadVersion(candidate);
        var b = ReadVersion(installed);

        return a is not null && b is not null && a > b;
    }

    private static Version ReadVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
            value = value[1..];

        var match = System.Text.RegularExpressions.Regex.Match(value, @"^\d+(\.\d+){0,3}");
        if (!match.Success)
            return null;

        // Version.TryParse needs at least "major.minor", and a one-part version
        // reads better as 6.0.0.0 than as "not a version"; missing parts are 0.
        var parts = match.Value.Split('.');
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]))
                return null;
        }

        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static bool IsUsable(PluginCatalogEntry entry)
    {
        return !string.IsNullOrWhiteSpace(entry.Id)
            && entry.Id.Length <= 100
            && IsPlainIdentifier(entry.Id)
            && !string.IsNullOrWhiteSpace(entry.Name)
            && entry.Size > 0
            && entry.Size <= MaxPluginBytes
            && entry.Sha256.Length == 64
            && IsHex(entry.Sha256)
            && Updater.IsTrustedDownloadUrl(entry.Url);
    }

    /// <summary>
    /// The id is matched against plugin folder names and shown in the panel, so
    /// it has to stay a plain identifier - no separators, no traversal.
    /// </summary>
    private static bool IsPlainIdentifier(string value)
    {
        if (value[0] is '.' or '-')
            return false;

        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                return false;
        }

        return true;
    }

    private static bool IsHex(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }

    private static string ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadLong(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : 0;

    private static IReadOnlyList<PluginCatalogEntry> TryReadCache(out string error)
    {
        error = string.Empty;

        try
        {
            if (!File.Exists(CachePath))
                return [];

            var json = File.ReadAllText(CachePath);
            return Parse(json, out _);
        }
        catch (Exception e)
        {
            error = e.Message;
            return [];
        }
    }

    private static void TryWriteCache(string json)
    {
        try
        {
            Directory.CreateDirectory(SettingHelper.LocalDataPath);
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // A catalogue that cannot be cached is still usable this session.
        }
    }
}
