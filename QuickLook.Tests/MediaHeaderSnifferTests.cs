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

using QuickLook.Plugin.Shared;
using System;
using System.Text;

namespace QuickLook.Tests;

/// <summary>
/// The video viewer classifies unknown extensions from their first bytes so a
/// TypeScript source named .ts, or a text/binary file with an unusual suffix,
/// never blocks CanHandle on a native MediaInfo open.
/// </summary>
internal class MediaHeaderSnifferTests
{
    public void TypeScriptSourceIsNotMedia()
    {
        var source = Utf8("import { preview } from \"./preview\";\n" +
                          "// A .ts source file must not be opened as MPEG-TS.\n" +
                          "export const value = preview();\n");

        Assert.Equal(MediaHeaderKind.NotMedia, MediaHeaderSniffer.Classify(source), "TypeScript source");
    }

    public void Utf8BomTextIsNotMedia()
    {
        var text = Utf8("plain text with a BOM");
        var data = new byte[text.Length + 3];
        data[0] = 0xEF;
        data[1] = 0xBB;
        data[2] = 0xBF;
        text.CopyTo(data, 3);

        Assert.Equal(MediaHeaderKind.NotMedia, MediaHeaderSniffer.Classify(data), "UTF-8 BOM text");
    }

    public void MpegTransportStreamIsVideo()
    {
        Assert.Equal(MediaHeaderKind.Video,
            MediaHeaderSniffer.Classify(TransportStream(188, 0)), "188-byte TS packets");
    }

    public void M2tsTransportStreamIsVideo()
    {
        Assert.Equal(MediaHeaderKind.Video,
            MediaHeaderSniffer.Classify(TransportStream(192, 4)), "192-byte M2TS packets");
    }

    public void TransportStreamWithLeadingJunkIsVideo()
    {
        Assert.Equal(MediaHeaderKind.Video,
            MediaHeaderSniffer.Classify(TransportStream(188, 0, junk: 3)), "TS after leading junk");
    }

    public void RealMediaIsAContainer()
    {
        var data = new byte[16];
        Encoding.ASCII.GetBytes(".RMF").CopyTo(data, 0);

        Assert.Equal(MediaHeaderKind.Container, MediaHeaderSniffer.Classify(data), "RealMedia header");
    }

    public void AsfIsAContainer()
    {
        var data = new byte[]
        {
            0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
            0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C,
        };

        Assert.Equal(MediaHeaderKind.Container, MediaHeaderSniffer.Classify(data), "ASF GUID");
    }

    public void MatroskaIsAContainer()
    {
        Assert.Equal(MediaHeaderKind.Container,
            MediaHeaderSniffer.Classify(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }), "EBML header");
    }

    public void Mp4IsAContainer()
    {
        Assert.Equal(MediaHeaderKind.Container,
            MediaHeaderSniffer.Classify(IsoBaseMedia("isom")), "MP4 ftyp");
    }

    public void HeifIsAnImage()
    {
        Assert.Equal(MediaHeaderKind.NotMedia,
            MediaHeaderSniffer.Classify(IsoBaseMedia("heic")), "HEIF image");
        Assert.Equal(MediaHeaderKind.NotMedia,
            MediaHeaderSniffer.Classify(IsoBaseMedia("avif")), "AVIF image");
    }

    public void WaveIsAudio()
    {
        Assert.Equal(MediaHeaderKind.Audio, MediaHeaderSniffer.Classify(Riff("WAVE")), "RIFF WAVE");
    }

    public void AviIsVideo()
    {
        Assert.Equal(MediaHeaderKind.Video, MediaHeaderSniffer.Classify(Riff("AVI ")), "RIFF AVI");
    }

    public void WebpIsAnImage()
    {
        Assert.Equal(MediaHeaderKind.NotMedia, MediaHeaderSniffer.Classify(Riff("WEBP")), "RIFF WebP");
    }

    public void FlacIsAudio()
    {
        Assert.Equal(MediaHeaderKind.Audio, MediaHeaderSniffer.Classify(Utf8("fLaC")), "FLAC");
    }

    public void OggIsAContainer()
    {
        Assert.Equal(MediaHeaderKind.Container, MediaHeaderSniffer.Classify(Utf8("OggS")), "Ogg");
    }

    public void FlvIsVideo()
    {
        Assert.Equal(MediaHeaderKind.Video,
            MediaHeaderSniffer.Classify(new byte[] { (byte)'F', (byte)'L', (byte)'V', 0x01 }), "FLV");
    }

    public void MpegProgramStreamIsVideo()
    {
        Assert.Equal(MediaHeaderKind.Video,
            MediaHeaderSniffer.Classify(new byte[] { 0x00, 0x00, 0x01, 0xBA }), "MPEG-PS");
    }

    public void Id3TagIsAudio()
    {
        Assert.Equal(MediaHeaderKind.Audio,
            MediaHeaderSniffer.Classify(new byte[]
            {
                (byte)'I', (byte)'D', (byte)'3', 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            }), "ID3 tag");
    }

    public void MpegAudioFrameIsAudio()
    {
        Assert.Equal(MediaHeaderKind.Audio,
            MediaHeaderSniffer.Classify(new byte[] { 0xFF, 0xFB, 0x90, 0x64 }), "MP3 frame");
    }

    public void JpegIsAnImage()
    {
        Assert.Equal(MediaHeaderKind.NotMedia,
            MediaHeaderSniffer.Classify(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }), "JPEG");
    }

    public void ZipIsNotMedia()
    {
        Assert.Equal(MediaHeaderKind.NotMedia,
            MediaHeaderSniffer.Classify(new byte[] { 0x50, 0x4B, 0x03, 0x04 }), "ZIP");
    }

    public void RandomBinaryIsUnknown()
    {
        Assert.Equal(MediaHeaderKind.Unknown,
            MediaHeaderSniffer.Classify(new byte[] { 0x00, 0x01, 0x02, 0xFF, 0x00, 0x10, 0x11, 0x12 }),
            "unrecognised binary");
    }

    public void EmptyFileIsNotMedia()
    {
        Assert.Equal(MediaHeaderKind.NotMedia,
            MediaHeaderSniffer.Classify(Array.Empty<byte>()), "empty file");
    }

    private static byte[] Utf8(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    private static byte[] Riff(string form)
    {
        var data = new byte[16];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(data, 0);
        Encoding.ASCII.GetBytes(form).CopyTo(data, 8);
        return data;
    }

    private static byte[] IsoBaseMedia(string brand)
    {
        var data = new byte[16];
        data[3] = 0x10;
        Encoding.ASCII.GetBytes("ftyp").CopyTo(data, 4);
        Encoding.ASCII.GetBytes(brand).CopyTo(data, 8);
        return data;
    }

    private static byte[] TransportStream(
        int stride,
        int syncOffset,
        int packets = 5,
        int junk = 0)
    {
        var data = new byte[junk + stride * packets];
        for (var packet = 0; packet < packets; packet++)
            data[junk + syncOffset + stride * packet] = 0x47;

        return data;
    }
}
