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

using PureSharpCompress.Archives;
using PureSharpCompress.Common;
using PureSharpCompress.Readers;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

public sealed record ExtractedArchiveEntry(string FilePath, string TemporaryDirectory);

public sealed class ArchiveEntryTooLargeException(long size)
    : Exception($"The selected file is too large to preview ({size} bytes).");

public sealed class ArchiveEntryNotFoundException(string path)
    : Exception($"The archive entry was not found: {path}");

/// <summary>
/// Extracts only the selected archive entry. Existing QuickLook viewers operate
/// on file paths, so a short-lived private copy is the safest way to reuse them
/// without unpacking the whole archive or changing every plugin to understand
/// virtual streams.
/// </summary>
public static class ArchiveEntryExtractor
{
    public const long MaxPreviewBytes = 512L * 1024L * 1024L;

    public static ExtractedArchiveEntry Extract(
        string archivePath,
        string entryPath,
        ulong expectedSize = 0,
        CancellationToken cancellationToken = default)
    {
        if (expectedSize > MaxPreviewBytes)
            throw new ArchiveEntryTooLargeException((long)expectedSize);

        using var fileStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var options = new ChardetReaderOptions();
        var useReader = new[] { ".tar.gz", ".tgz", ".tar.bz2", ".tar.lz", ".tar.xz" }
            .Any(ext => archivePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

        if (useReader)
        {
            using var reader = ReaderFactory.Open(fileStream, options);
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.Entry.IsDirectory && PathsEqual(reader.Entry.Key, entryPath))
                    return ExtractEntry(
                        reader.Entry,
                        reader.OpenEntryStream,
                        entryPath,
                        cancellationToken);
            }
        }
        else
        {
            using var archive = ArchiveFactory.Open(fileStream, options);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.IsDirectory && PathsEqual(entry.Key, entryPath))
                    return ExtractEntry(
                        entry,
                        entry.OpenEntryStream,
                        entryPath,
                        cancellationToken);
            }
        }

        throw new ArchiveEntryNotFoundException(entryPath);
    }

    internal static string NormalizeArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return string.Join(
            "\\",
            path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => part != "."));
    }

    private static bool PathsEqual(string archiveEntryPath, string selectedPath)
    {
        return string.Equals(
            NormalizeArchivePath(archiveEntryPath),
            NormalizeArchivePath(selectedPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static ExtractedArchiveEntry ExtractEntry(
        IEntry entry,
        Func<Stream> openEntryStream,
        string entryPath,
        CancellationToken cancellationToken)
    {
        if (entry.IsEncrypted)
            throw new UnauthorizedAccessException("Encrypted archive entries cannot be previewed yet.");

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "QuickLookNext.ArchivePreview",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);

        var fileName = GetSafeFileName(entryPath);
        var destination = Path.Combine(temporaryDirectory, fileName);

        try
        {
            using var source = openEntryStream();
            using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);

            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += read;
                if (total > MaxPreviewBytes)
                    throw new ArchiveEntryTooLargeException(total);
                target.Write(buffer, 0, read);
            }

            if (entry.LastModifiedTime is { } modified)
            {
                try
                {
                    File.SetLastWriteTime(destination, modified);
                }
                catch
                {
                    // Metadata is cosmetic; extraction itself already succeeded.
                }
            }

            return new ExtractedArchiveEntry(destination, temporaryDirectory);
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    private static string GetSafeFileName(string entryPath)
    {
        var name = Path.GetFileName(entryPath.Replace('/', '\\'));
        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
            name = "archive-entry.bin";

        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        return string.IsNullOrWhiteSpace(safe) ? "archive-entry.bin" : safe;
    }

    public static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A viewer may still be releasing a native file handle. The next
            // archive preview or operating-system temp cleanup can remove it.
        }
    }

    /// <summary>
    /// Removes crash leftovers without touching directories that may belong to
    /// another QuickLook process that is still previewing an archive.
    /// </summary>
    public static void CleanupStalePreviewDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "QuickLookNext.ArchivePreview");
        if (!Directory.Exists(root))
            return;

        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    TryDeleteDirectory(directory);
            }
        }
        catch
        {
            // Temp cleanup is opportunistic and must never block archive preview.
        }
    }

    /// <summary>
    /// Some viewers release native handles shortly after Cleanup returns. Retry
    /// in the background so a successful preview does not leave a private copy.
    /// </summary>
    public static void DeleteDirectoryWhenUnused(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                TryDeleteDirectory(path);
                if (!Directory.Exists(path))
                    return;

                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)));
            }
        });
    }
}
