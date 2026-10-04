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
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace QuickLookNext.Helpers;

internal enum PluginInstallStatus
{
    Installed,

    /// <summary>The download was fine but the package is not installable.</summary>
    Rejected,

    Failed,
}

internal sealed record PluginInstallResult(
    PluginInstallStatus Status, string Folder, string Version, string Message);

/// <summary>
/// v5.6.0: downloads a catalogue entry and installs it into the user plugin
/// folder - the same place, and the same metadata contract, that the
/// PluginInstaller plugin uses for a hand-downloaded .qlplugin.
/// <para>
/// The package is verified twice before a single file is written: its length
/// and its SHA-256 have to match what the catalogue recorded. A mismatch aborts
/// the install and deletes the download, so a tampered or half-finished file
/// never reaches the plugin folder.
/// </para>
/// </summary>
internal static class PluginInstallService
{
    /// <summary>Same metadata file the PluginInstaller plugin reads.</summary>
    private const string MetadataEntry = "QuickLook.Plugin.Metadata.config";

    private const string NamespacePrefix = "QuickLook.Plugin.";

    internal static Task<PluginInstallResult> InstallAsync(
        PluginCatalogEntry entry, IProgress<Updater.DownloadProgress> progress, CancellationToken cancellation)
    {
        return Task.Run(() => Install(entry, progress, cancellation), cancellation);
    }

    /// <summary>
    /// Only ever a folder name inside the user plugin folder: it has to be a
    /// plugin namespace, with no separators and no traversal.
    /// </summary>
    internal static bool IsValidNamespace(string ns)
    {
        if (string.IsNullOrWhiteSpace(ns) || ns.Length > 120)
            return false;

        if (!ns.StartsWith(NamespacePrefix, StringComparison.Ordinal))
            return false;

        // "QuickLook.Plugin." on its own would install straight into the user
        // plugin folder, so there has to be a name after the prefix.
        if (ns.Length <= NamespacePrefix.Length)
            return false;

        if (ns.Contains("..", StringComparison.Ordinal) || ns.Contains(':'))
            return false;

        foreach (var c in ns)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                return false;
        }

        return true;
    }

    private static PluginInstallResult Install(
        PluginCatalogEntry entry, IProgress<Updater.DownloadProgress> progress, CancellationToken cancellation)
    {
        var staging = Path.Combine(Path.GetTempPath(), "QuickLookNext.PluginDownload", entry.Asset);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            Download(entry, staging, progress, cancellation);
            Verify(entry, staging);

            var (ns, version) = ReadMetadata(staging);
            if (!IsValidNamespace(ns))
            {
                return new PluginInstallResult(PluginInstallStatus.Rejected, string.Empty, version,
                    $"\"{entry.Name}\" does not declare a valid plugin namespace, so it was not installed.");
            }

            var target = Path.Combine(App.UserPluginPath, ns);
            Extract(staging, target);

            // The running app still holds the previous version of this plugin in
            // memory; drop it so the next preview picks up the new assembly.
            var manager = PluginManager.GetInstance();
            manager.RemovePluginsUnder(target);

            return new PluginInstallResult(PluginInstallStatus.Installed, target, version, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new PluginInstallResult(PluginInstallStatus.Failed, string.Empty, string.Empty, "Cancelled.");
        }
        catch (Exception e)
        {
            ProcessHelper.WriteLog($"Plugin install of {entry.Id} failed: {e}");
            return new PluginInstallResult(PluginInstallStatus.Failed, string.Empty, string.Empty, e.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(staging))
                    File.Delete(staging);
            }
            catch
            {
                // A leftover in %TEMP% is harmless.
            }
        }
    }

    private static void Download(PluginCatalogEntry entry, string targetPath,
        IProgress<Updater.DownloadProgress> progress, CancellationToken cancellation)
    {
        if (!Updater.IsTrustedDownloadUrl(entry.Url))
            throw new InvalidDataException("The catalogue points at a host this app does not download from.");

        using var client = Updater.CreateHttpClient(TimeSpan.FromMinutes(5));
        using var response = client
            .GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead, cancellation)
            .GetAwaiter()
            .GetResult();

        response.EnsureSuccessStatusCode();

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > PluginCatalog.MaxPluginBytes)
            throw new InvalidDataException("The package is larger than this app will download.");

        progress?.Report(new Updater.DownloadProgress(0, declaredLength ?? entry.Size));

        using (var content = response.Content.ReadAsStream(cancellation))
        using (var file = File.Create(targetPath))
        {
            Updater.CopyWithLimit(content, file, PluginCatalog.MaxPluginBytes,
                declaredLength ?? entry.Size, progress, cancellation);
        }
    }

    private static void Verify(PluginCatalogEntry entry, string path)
    {
        var length = new FileInfo(path).Length;
        if (length != entry.Size)
        {
            throw new InvalidDataException(
                $"Downloaded {length} bytes, the catalogue lists {entry.Size}.");
        }

        using var file = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(file));

        if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded package does not match the recorded SHA-256.");
    }

    private static (string Namespace, string Version) ReadMetadata(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Read);
        using var entry = zip.GetEntry(MetadataEntry)?.Open()
            ?? throw new InvalidDataException("The package has no plugin metadata.");

        var document = new XmlDocument();
        try
        {
            document.Load(entry);
        }
        catch (XmlException)
        {
            throw new InvalidDataException("The package metadata is not valid XML.");
        }

        var ns = document.SelectSingleNode("/Metadata/Namespace")?.InnerText?.Trim() ?? string.Empty;
        var version = document.SelectSingleNode("/Metadata/Version")?.InnerText?.Trim() ?? string.Empty;

        return (ns, version);
    }

    /// <summary>
    /// Files of an older version of the same plugin are usually still loaded, so
    /// they cannot be overwritten. They are parked exactly the way an uninstall
    /// parks them (<c>*.uninstalled</c>), which the next start cleans up.
    /// </summary>
    private static void Extract(string package, string targetFolder)
    {
        if (Directory.Exists(targetFolder))
        {
            try
            {
                Directory.Delete(targetFolder, recursive: true);
            }
            catch (Exception)
            {
                var pending = targetFolder.TrimEnd('\\', '/') + ".uninstalled";
                if (Directory.Exists(pending))
                    Directory.Delete(pending, recursive: true);
                Directory.Move(targetFolder, pending);
            }
        }

        Directory.CreateDirectory(targetFolder);
        ZipFile.ExtractToDirectory(package, targetFolder);
    }
}
