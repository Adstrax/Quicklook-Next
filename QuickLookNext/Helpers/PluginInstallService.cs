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
        // Keep the download and the directory that is moved into place on the
        // same volume. A portable installation can live on D:\ while %TEMP% is
        // on C:\, and Directory.Move cannot cross volumes.
        var pluginRoot = Path.TrimEndingDirectorySeparator(App.UserPluginPath);
        var dataRoot = Path.GetDirectoryName(pluginRoot)!;
        var stagingRoot = Path.Combine(dataRoot, "PluginInstallStaging");
        var stagingFolder = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        var safeAsset = Path.GetFileName(entry.Asset);
        var staging = Path.Combine(stagingFolder, safeAsset);
        var extracted = Path.Combine(stagingFolder, "extracted");

        try
        {
            if (string.IsNullOrWhiteSpace(safeAsset) ||
                !string.Equals(safeAsset, entry.Asset, StringComparison.Ordinal) ||
                !safeAsset.EndsWith(".qlplugin", StringComparison.OrdinalIgnoreCase))
            {
                return new PluginInstallResult(
                    PluginInstallStatus.Rejected, string.Empty, string.Empty,
                    "The catalogue contains an unsafe plugin asset name.");
            }

            Directory.CreateDirectory(stagingFolder);
            Download(entry, staging, progress, cancellation);
            VerifyDownload(staging, entry.Size, entry.Sha256);

            var (ns, version) = ReadMetadata(staging);
            if (!IsValidNamespace(ns))
            {
                return new PluginInstallResult(PluginInstallStatus.Rejected, string.Empty, version,
                    $"\"{entry.Name}\" does not declare a valid plugin namespace, so it was not installed.");
            }

            var target = Path.Combine(App.UserPluginPath, ns);
            ExtractToStaging(staging, extracted);

            var pluginDll = Path.Combine(extracted, ns + ".dll");
            if (!File.Exists(pluginDll))
            {
                return new PluginInstallResult(
                    PluginInstallStatus.Rejected, string.Empty, version,
                    $"\"{entry.Name}\" does not contain the expected {ns}.dll.");
            }

            ReplaceInstalledPlugin(extracted, target);

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
                if (Directory.Exists(stagingFolder))
                    Directory.Delete(stagingFolder, recursive: true);
            }
            catch
            {
                // A leftover in the unique staging folder is harmless and can be
                // removed by the normal temp cleanup.
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

    /// <summary>
    /// The download is only accepted when both the length and the hash match
    /// what the catalogue recorded, so a truncated, replaced or tampered file
    /// never reaches the plugin folder.
    /// </summary>
    internal static void VerifyDownload(string path, long expectedSize, string expectedSha256)
    {
        var length = new FileInfo(path).Length;
        if (length != expectedSize)
        {
            throw new InvalidDataException(
                $"Downloaded {length} bytes, the catalogue lists {expectedSize}.");
        }

        using var file = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(file));

        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded package does not match the recorded SHA-256.");
    }

    /// <summary>Reads the namespace and version a .qlplugin declares for itself.</summary>
    internal static (string Namespace, string Version) ReadMetadata(string path)
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
    private static void ExtractToStaging(string package, string stagingFolder)
    {
        Directory.CreateDirectory(stagingFolder);
        ZipFile.ExtractToDirectory(package, stagingFolder);
    }

    /// <summary>
    /// Swaps a fully extracted package into place after keeping the old directory
    /// recoverable. A crash between the two moves is repaired on the next start:
    /// the backup is restored when the target is missing, otherwise it is removed.
    /// </summary>
    internal static void ReplaceInstalledPlugin(string stagingFolder, string targetFolder)
    {
        // A fresh portable profile has UserData but not UserData\QuickLook.Plugin
        // yet. Directory.Move does not create the destination's parent, and its
        // DirectoryNotFound exception used to make every first plugin install fail.
        var targetParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(targetFolder));
        if (!string.IsNullOrEmpty(targetParent))
            Directory.CreateDirectory(targetParent);

        string backup = null;

        if (Directory.Exists(targetFolder))
        {
            var pluginRoot = Path.TrimEndingDirectorySeparator(App.UserPluginPath);
            var backupRoot = Path.Combine(
                Path.GetDirectoryName(pluginRoot)!,
                "PluginRollback");
            Directory.CreateDirectory(backupRoot);

            backup = Path.Combine(backupRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            File.WriteAllText(
                Path.Combine(backup, ".original"),
                Path.GetFileName(targetFolder));

            try
            {
                Directory.Move(targetFolder, Path.Combine(backup, "plugin"));
            }
            catch
            {
                try
                {
                    Directory.Delete(backup, recursive: true);
                }
                catch
                {
                    // The main startup cleanup removes an empty/marked backup.
                }

                backup = null;
                throw;
            }
        }

        try
        {
            Directory.Move(stagingFolder, targetFolder);
        }
        catch
        {
            if (backup is not null &&
                !Directory.Exists(targetFolder) &&
                Directory.Exists(Path.Combine(backup, "plugin")))
            {
                Directory.Move(Path.Combine(backup, "plugin"), targetFolder);

                try
                {
                    Directory.Delete(backup, recursive: true);
                }
                catch
                {
                    // The next startup sees the restored target and removes it.
                }
            }

            throw;
        }

        if (backup is not null)
        {
            try
            {
                Directory.Delete(backup, recursive: true);
            }
            catch
            {
                // The old assemblies are often still loaded. The next startup
                // removes the backup after confirming the new target exists.
            }
        }
    }
}
