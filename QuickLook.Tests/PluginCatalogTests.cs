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

using QuickLookNext.Helpers;

namespace QuickLook.Tests;

/// <summary>
/// v5.6.0: the plugin catalogue is fetched over the network and its entries end
/// up as a download that gets extracted into the plugin folder, so every field
/// it carries is a trust boundary. These tests cover the two decisions that
/// keep that safe: which entries are allowed through, and which folder a
/// package is allowed to install itself into.
/// </summary>
internal class PluginCatalogTests
{
    private const string GoodHash = "347482bcb91eed73a1340603535a2c797851d56f4cb7a3c8f7ca7f985c495e2b";

    private static string Entry(
        string id = "QuickLook.Plugin.ApkViewer",
        string name = "ApkViewer",
        string size = "770991",
        string sha256 = GoodHash,
        string url = "https://github.com/canheo136/QuickLook.Plugin.ApkViewer/releases/download/1.3.6/QuickLook.Plugin.ApkViewer.qlplugin")
    {
        return $$"""
        {
          "schema": 1,
          "plugins": [
            {
              "id": "{{id}}",
              "name": "{{name}}",
              "publisher": "canheo136",
              "description": "Preview Android package `.apk`",
              "repoUrl": "https://github.com/canheo136/QuickLook.Plugin.ApkViewer",
              "version": "1.3.6",
              "asset": "QuickLook.Plugin.ApkViewer.qlplugin",
              "size": {{size}},
              "sha256": "{{sha256}}",
              "url": "{{url}}",
              "somethingAddedLater": true
            }
          ]
        }
        """;
    }

    public void AVerifiedEntryIsKept()
    {
        var entries = PluginCatalog.Parse(Entry(), out var rejected);

        Assert.Equal(1, entries.Count, "one entry");
        Assert.Equal(0, rejected, "nothing rejected");
        Assert.Equal("QuickLook.Plugin.ApkViewer", entries[0].Id, "id");
        Assert.Equal(770991L, entries[0].Size, "size");
        Assert.Equal("753 KB", entries[0].SizeText, "size text");
    }

    public void AnUnknownFieldIsNotAReasonToDropTheEntry()
    {
        // The generator may add fields later; an older app must keep working.
        var entries = PluginCatalog.Parse(Entry(), out _);

        Assert.Equal(1, entries.Count, "still one entry");
    }

    public void ADownloadFromAnywhereButGitHubIsRejected()
    {
        Assert.Equal(0, PluginCatalog.Parse(
            Entry(url: "https://plugins.example.com/ApkViewer.qlplugin"), out var foreign).Count,
            "foreign host");
        Assert.Equal(1, foreign, "counted as rejected");

        Assert.Equal(0, PluginCatalog.Parse(
            Entry(url: "http://github.com/canheo136/x/releases/download/1.3.6/x.qlplugin"), out _).Count,
            "plain http");
    }

    public void AnUnpinnedPackageIsRejected()
    {
        Assert.Equal(0, PluginCatalog.Parse(Entry(sha256: "abc123"), out _).Count, "short hash");
        Assert.Equal(0, PluginCatalog.Parse(
            Entry(sha256: "zz3482bcb91eed73a1340603535a2c797851d56f4cb7a3c8f7ca7f985c495e2b"), out _).Count,
            "not hexadecimal");
    }

    public void AnImplausibleSizeIsRejected()
    {
        Assert.Equal(0, PluginCatalog.Parse(Entry(size: "0"), out _).Count, "zero bytes");
        Assert.Equal(0, PluginCatalog.Parse(Entry(size: "-1"), out _).Count, "negative");
        Assert.Equal(0, PluginCatalog.Parse(Entry(size: "999999999999"), out _).Count, "far too large");
    }

    public void AnIdThatCouldEscapeThePluginFolderIsRejected()
    {
        Assert.Equal(0, PluginCatalog.Parse(Entry(id: "../../Windows"), out _).Count, "traversal");
        Assert.Equal(0, PluginCatalog.Parse(Entry(id: @"..\..\Windows"), out _).Count, "backslash traversal");
        Assert.Equal(0, PluginCatalog.Parse(Entry(id: "QuickLook.Plugin.A/../B"), out _).Count, "separator");
        Assert.Equal(0, PluginCatalog.Parse(Entry(id: string.Empty), out _).Count, "empty");
    }

    public void AMalformedDocumentYieldsNothingInsteadOfThrowing()
    {
        Assert.Equal(0, PluginCatalog.Parse("{ not json", out _).Count, "broken json");
        Assert.Equal(0, PluginCatalog.Parse("{\"plugins\": {}}", out _).Count, "plugins is not an array");
        Assert.Equal(0, PluginCatalog.Parse(string.Empty, out _).Count, "empty document");
    }

    public void OnlyTheLeadingNumberOfAVersionDecidesAnUpdate()
    {
        Assert.True(PluginCatalog.IsNewerThan("1.3.7", "1.3.6.0"), "newer patch");
        Assert.True(PluginCatalog.IsNewerThan("v1.0.4", "1.0.3"), "v prefix");
        Assert.True(PluginCatalog.IsNewerThan("6", "5.9"), "major");

        Assert.False(PluginCatalog.IsNewerThan("1.3.6", "1.3.6"), "same version");
        Assert.False(PluginCatalog.IsNewerThan("1.3.5", "1.3.6"), "older");

        // A channel suffix ("v2.0.0-performance") is compared by its leading
        // number, and the suffix never invents an update of its own.
        Assert.True(PluginCatalog.IsNewerThan("v2.0.0-performance", "1.0.0"), "suffixed candidate");
        Assert.False(PluginCatalog.IsNewerThan("v2.0.0-performance", "2.0.0"), "same number, suffixed");
        Assert.False(PluginCatalog.IsNewerThan("1.0.0", "unknown"), "unparseable installed version");
    }

    public void OnlyAPluginNamespaceCanBeAnInstallTarget()
    {
        Assert.True(PluginInstallService.IsValidNamespace("QuickLook.Plugin.ApkViewer"), "plain namespace");
        Assert.True(PluginInstallService.IsValidNamespace("QuickLook.Plugin.FolderViewer.For.Everything"), "dotted");

        Assert.False(PluginInstallService.IsValidNamespace("QuickLook.Plugin.../.."), "traversal");
        Assert.False(PluginInstallService.IsValidNamespace(@"QuickLook.Plugin.A\B"), "separator");
        Assert.False(PluginInstallService.IsValidNamespace("QuickLook.Plugin.A:B"), "drive colon");
        Assert.False(PluginInstallService.IsValidNamespace("SomethingElse.Viewer"), "not a plugin namespace");
        Assert.False(PluginInstallService.IsValidNamespace("QuickLook.Plugin."), "prefix only");
        Assert.False(PluginInstallService.IsValidNamespace(null), "null");
    }
}
