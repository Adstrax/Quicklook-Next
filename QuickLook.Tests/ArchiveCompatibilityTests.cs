// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using PureSharpCompress.Common;
using QuickLook.Plugin.ArchiveViewer.ArchiveFile;
using System;
using System.IO;

namespace QuickLook.Tests;

internal class ArchiveCompatibilityTests
{
    public void ClassifiesEncryptedFailuresThroughWrappersAndAggregates()
    {
        var encrypted = new PureSharpCompress.Common.CryptographicException("password required");
        var wrapped = new Exception("reader failed", encrypted);

        Assert.Equal(
            ArchivePreviewFailure.Encrypted,
            ArchivePreviewFailureClassifier.Classify(wrapped),
            "a wrapped cryptographic error");
        Assert.Equal(
            ArchivePreviewFailure.Encrypted,
            ArchivePreviewFailureClassifier.Classify(new AggregateException(wrapped)),
            "an aggregate cryptographic error");
    }

    public void DistinguishesMissingVolumesFromDamagedArchives()
    {
        Assert.Equal(
            ArchivePreviewFailure.Incomplete,
            ArchivePreviewFailureClassifier.Classify(new IncompleteArchiveException("missing end")),
            "truncated archive");
        Assert.Equal(
            ArchivePreviewFailure.Incomplete,
            ArchivePreviewFailureClassifier.Classify(new MultiVolumeExtractionException("missing volume")),
            "missing split volume");
        Assert.Equal(
            ArchivePreviewFailure.InvalidFormat,
            ArchivePreviewFailureClassifier.Classify(new InvalidDataException("bad header")),
            "damaged data");
        Assert.Equal(
            ArchivePreviewFailure.Other,
            ArchivePreviewFailureClassifier.Classify(new UnauthorizedAccessException("denied")),
            "an unrelated access error is not called encrypted");
    }

    public void SanitizesReservedAndUnsafeArchiveFileNames()
    {
        Assert.Equal("_CON.txt", ArchiveEntryExtractor.GetSafeFileName(@"..\CON.txt"),
            "reserved Windows device names are prefixed");
        Assert.Equal("_LPT9", ArchiveEntryExtractor.GetSafeFileName("folder/LPT9"),
            "reserved name without an extension");
        Assert.Equal("bad_name_.txt", ArchiveEntryExtractor.GetSafeFileName("bad:name?.txt"),
            "invalid file-name characters are replaced");
        Assert.Equal("name", ArchiveEntryExtractor.GetSafeFileName("folder/name.  "),
            "trailing dots and spaces are removed");
    }

    public void KeepsTheExtensionWhenAnEntryNameMustBeShortened()
    {
        var name = new string('a', 260) + ".pdf";
        var safe = ArchiveEntryExtractor.GetSafeFileName(@"deep\nested\" + name);

        Assert.True(safe.Length <= 128, "temporary file name stays below the Windows length budget");
        Assert.Equal(".pdf", Path.GetExtension(safe), "the viewer still receives the original extension");
    }
}
