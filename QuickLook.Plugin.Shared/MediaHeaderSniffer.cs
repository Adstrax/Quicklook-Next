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

using System;
using System.IO;
using System.Text.Unicode;

namespace QuickLook.Plugin.Shared;

/// <summary>
/// Classifies the first few kilobytes of a file without opening MediaInfo.
/// The video viewer calls this for extensions it does not know, so a
/// TypeScript source named ".ts" or a text file with an unusual extension is
/// rejected before the native media parser ever sees it.
/// </summary>
public enum MediaHeaderKind
{
    /// <summary>The content is recognisably not audio or video.</summary>
    NotMedia,

    /// <summary>Binary content without a known media signature; ask MediaInfo.</summary>
    Unknown,

    Audio,
    Video,

    /// <summary>A container that may hold audio, video, or both.</summary>
    Container,
}

public static class MediaHeaderSniffer
{
    private const int HeaderLength = 8192;
    private const int TextSampleLength = 4096;

    public static MediaHeaderKind Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return MediaHeaderKind.Unknown;

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            if (stream.Length == 0)
                return MediaHeaderKind.NotMedia;

            var length = (int)Math.Min(stream.Length, HeaderLength);
            var header = new byte[length];
            var read = 0;

            while (read < length)
            {
                var count = stream.Read(header, read, length - read);
                if (count <= 0)
                    break;
                read += count;
            }

            return read == 0
                ? MediaHeaderKind.Unknown
                : Classify(header.AsSpan(0, read));
        }
        catch
        {
            // The file may be locked, missing, or a special device. Let the
            // existing MediaInfo path make the final decision.
            return MediaHeaderKind.Unknown;
        }
    }

    public static MediaHeaderKind Classify(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
            return MediaHeaderKind.NotMedia;

        // UTF text, including a BOM, is never a media container for our
        // purposes. The BOM check also covers UTF-16 sources.
        if (StartsWith(header, [0xEF, 0xBB, 0xBF]) ||
            StartsWith(header, [0xFF, 0xFE]) ||
            StartsWith(header, [0xFE, 0xFF]))
            return MediaHeaderKind.NotMedia;

        if (StartsWith(header, "RIFF"u8))
        {
            if (header.Length < 12)
                return MediaHeaderKind.Unknown;

            var form = header.Slice(8, 4);
            if (form.SequenceEqual("WAVE"u8)) return MediaHeaderKind.Audio;
            if (form.SequenceEqual("AVI "u8) || form.SequenceEqual("AVIX"u8)) return MediaHeaderKind.Video;
            if (form.SequenceEqual("RMID"u8)) return MediaHeaderKind.Audio;
            if (form.SequenceEqual("CDXA"u8)) return MediaHeaderKind.Video;
            if (form.SequenceEqual("WEBP"u8)) return MediaHeaderKind.NotMedia;
            return MediaHeaderKind.Unknown;
        }

        if (StartsWith(header, "FORM"u8))
        {
            if (header.Length >= 12)
            {
                var form = header.Slice(8, 4);
                if (form.SequenceEqual("AIFF"u8) || form.SequenceEqual("AIFC"u8))
                    return MediaHeaderKind.Audio;
            }

            return MediaHeaderKind.Unknown;
        }

        if (StartsWith(header, "fLaC"u8)) return MediaHeaderKind.Audio;
        if (StartsWith(header, "OggS"u8)) return MediaHeaderKind.Container;
        if (StartsWith(header, "MThd"u8)) return MediaHeaderKind.Audio;
        if (IsId3Tag(header)) return MediaHeaderKind.Audio;
        if (StartsWith(header, "wvpk"u8)) return MediaHeaderKind.Audio;
        if (StartsWith(header, "MAC "u8)) return MediaHeaderKind.Audio;
        if (StartsWith(header, "#!AMR"u8)) return MediaHeaderKind.Audio;
        if (StartsWith(header, "NIST_1A"u8)) return MediaHeaderKind.Audio;
        if (StartsWith(header, "caff"u8)) return MediaHeaderKind.Audio;

        // EBML: Matroska / WebM.
        if (StartsWith(header, [0x1A, 0x45, 0xDF, 0xA3]))
            return MediaHeaderKind.Container;

        if (IsIsoBaseMedia(header, out var isoKind))
            return isoKind;

        // ASF GUID (also used by WMV/WMA).
        if (StartsWith(header,
                [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
                 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C]))
            return MediaHeaderKind.Container;

        // RealMedia (.rm / .rmvb).
        if (StartsWith(header, ".RMF"u8))
            return MediaHeaderKind.Container;

        if (StartsWith(header, [0x46, 0x4C, 0x56, 0x01])) return MediaHeaderKind.Video; // FLV
        if (StartsWith(header, [0x00, 0x00, 0x01, 0xBA])) return MediaHeaderKind.Video; // MPEG-PS
        if (StartsWith(header, "YUV4MPEG2"u8)) return MediaHeaderKind.Video;
        if (StartsWith(header, "DKIF"u8)) return MediaHeaderKind.Video;                // IVF
        if (StartsWith(header, [0x0B, 0x77])) return MediaHeaderKind.Audio;            // AC-3
        if (StartsWith(header, [0x7F, 0xFE, 0x80, 0x01])) return MediaHeaderKind.Audio; // DTS
        if (IsMpegTransportStream(header)) return MediaHeaderKind.Video;
        if (IsMpegAudioFrame(header)) return MediaHeaderKind.Audio;

        if (IsKnownNonMedia(header)) return MediaHeaderKind.NotMedia;
        if (LooksLikeText(header)) return MediaHeaderKind.NotMedia;

        return MediaHeaderKind.Unknown;
    }

    private static bool IsIsoBaseMedia(ReadOnlySpan<byte> header, out MediaHeaderKind kind)
    {
        kind = MediaHeaderKind.Unknown;
        if (header.Length < 12)
            return false;

        var box = header.Slice(4, 4);
        if (box.SequenceEqual("ftyp"u8))
        {
            kind = IsImageBrand(header.Slice(8, 4))
                ? MediaHeaderKind.NotMedia
                : MediaHeaderKind.Container;
            return true;
        }

        if (box.SequenceEqual("moov"u8) ||
            box.SequenceEqual("mdat"u8) ||
            box.SequenceEqual("wide"u8) ||
            box.SequenceEqual("free"u8) ||
            box.SequenceEqual("skip"u8) ||
            box.SequenceEqual("styp"u8))
        {
            kind = MediaHeaderKind.Container;
            return true;
        }

        return false;
    }

    private static bool IsImageBrand(ReadOnlySpan<byte> brand)
    {
        // HEIF/AVIF/JPEG-2000/CR3 use the same ISO base-media box layout as
        // MP4. They must not be claimed by the video viewer just because the
        // extension is unknown.
        return brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
               brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8) ||
               brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8) ||
               brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8) ||
               brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8) ||
               brand.SequenceEqual("jxl "u8) || brand.SequenceEqual("jp2 "u8) ||
               brand.SequenceEqual("crx "u8);
    }

    private static bool IsId3Tag(ReadOnlySpan<byte> header)
    {
        if (header.Length < 10)
            return false;

        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3')
            return false;

        if (header[3] is < 2 or > 4 || header[4] == 0xFF)
            return false;

        // ID3 sizes are sync-safe: every size byte has its high bit clear.
        return (header[6] | header[7] | header[8] | header[9]) < 0x80;
    }

    private static bool IsMpegAudioFrame(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
            return false;

        if (header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
            return false;

        var version = (header[1] >> 3) & 0x03;
        var layer = (header[1] >> 1) & 0x03;
        var bitrate = (header[2] >> 4) & 0x0F;
        var sampleRate = (header[2] >> 2) & 0x03;

        return version != 0x01 && layer != 0x00 && bitrate != 0x0F && sampleRate != 0x03;
    }

    private static bool IsMpegTransportStream(ReadOnlySpan<byte> header)
    {
        ReadOnlySpan<int> strides = [188, 192, 204];

        foreach (var stride in strides)
        {
            var startLimit = Math.Min(stride, header.Length);
            for (var start = 0; start < startLimit; start++)
            {
                if (header[start] != 0x47)
                    continue;

                if (start + stride * 4 >= header.Length)
                    continue;

                var matches = true;
                for (var packet = 1; packet <= 4; packet++)
                {
                    if (header[start + stride * packet] != 0x47)
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return true;
            }
        }

        return false;
    }

    private static bool IsKnownNonMedia(ReadOnlySpan<byte> header)
    {
        return StartsWith(header, [0x89, (byte)'P', (byte)'N', (byte)'G']) ||
               StartsWith(header, [0xFF, 0xD8, 0xFF]) ||
               StartsWith(header, "GIF87a"u8) ||
               StartsWith(header, "GIF89a"u8) ||
               StartsWith(header, [0x42, 0x4D]) ||
               StartsWith(header, [0x49, 0x49, 0x2A, 0x00]) ||
               StartsWith(header, [0x4D, 0x4D, 0x00, 0x2A]) ||
               StartsWith(header, [0x50, 0x4B, 0x03, 0x04]) ||
               StartsWith(header, [0x50, 0x4B, 0x05, 0x06]) ||
               StartsWith(header, [0x50, 0x4B, 0x07, 0x08]) ||
               StartsWith(header, "Rar!"u8) ||
               StartsWith(header, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]) ||
               StartsWith(header, "%PDF"u8) ||
               StartsWith(header, [0x7F, (byte)'E', (byte)'L', (byte)'F']) ||
               StartsWith(header, "SQLite format 3\0"u8) ||
               StartsWith(header, [0xD0, 0xCF, 0x11, 0xE0]) ||
               StartsWith(header, [0x1F, 0x8B]) ||
               StartsWith(header, [0x42, 0x5A, 0x68]) ||
               StartsWith(header, [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]) ||
               StartsWith(header, [0x4D, 0x5A]);
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> header)
    {
        var sample = header.Length > TextSampleLength
            ? header[..TextSampleLength]
            : header;

        if (sample.IsEmpty || sample.Contains((byte)0))
            return false;

        if (!Utf8.IsValid(sample))
            return false;

        var printable = 0;
        foreach (var value in sample)
        {
            if (value is 0x09 or 0x0A or 0x0D ||
                value is >= 0x20 and < 0x7F ||
                value >= 0x80)
                printable++;
        }

        return printable * 100 >= sample.Length * 95;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
    {
        return data.Length >= prefix.Length &&
               data[..prefix.Length].SequenceEqual(prefix);
    }
}
