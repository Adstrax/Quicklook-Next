// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using PureSharpCompress.Common;
using System;
using System.IO;

namespace QuickLook.Plugin.ArchiveViewer.ArchiveFile;

internal enum ArchivePreviewFailure
{
    Other,
    Encrypted,
    TooLarge,
    MissingEntry,
    Incomplete,
    InvalidFormat,
}

/// <summary>
/// Maps compression-library failures to the small set of messages the preview
/// can explain to a user. The classification follows wrapped inner exceptions
/// because archive readers often add their own context around the original
/// format or volume error.
/// </summary>
internal static class ArchivePreviewFailureClassifier
{
    internal static ArchivePreviewFailure Classify(Exception exception)
    {
        for (var depth = 0; exception != null && depth < 8; depth++)
        {
            var classified = ClassifyOne(exception);
            if (classified != ArchivePreviewFailure.Other)
                return classified;

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    classified = Classify(inner);
                    if (classified != ArchivePreviewFailure.Other)
                        return classified;
                }
            }

            exception = exception.InnerException;
        }

        return ArchivePreviewFailure.Other;
    }

    private static ArchivePreviewFailure ClassifyOne(Exception exception)
    {
        return exception switch
        {
            ArchiveEntryTooLargeException => ArchivePreviewFailure.TooLarge,
            ArchiveEntryEncryptedException => ArchivePreviewFailure.Encrypted,
            PureSharpCompress.Common.CryptographicException => ArchivePreviewFailure.Encrypted,
            IncompleteArchiveException => ArchivePreviewFailure.Incomplete,
            MultipartStreamRequiredException => ArchivePreviewFailure.Incomplete,
            MultiVolumeExtractionException => ArchivePreviewFailure.Incomplete,
            ArchiveEntryNotFoundException => ArchivePreviewFailure.MissingEntry,
            InvalidFormatException => ArchivePreviewFailure.InvalidFormat,
            ArchiveException => ArchivePreviewFailure.InvalidFormat,
            SharpCompressException => ArchivePreviewFailure.InvalidFormat,
            InvalidDataException => ArchivePreviewFailure.InvalidFormat,
            EndOfStreamException => ArchivePreviewFailure.InvalidFormat,
            _ => ArchivePreviewFailure.Other,
        };
    }
}
