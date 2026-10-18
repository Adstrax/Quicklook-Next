// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Text;

namespace QuickLookNext;

/// <summary>
/// Names the single-instance mutex and pipe for this process. Normal runs use
/// the historical names. A development build can set QL_INSTANCE_SUFFIX so its
/// measurement instance never forwards a request to the user's installed tray
/// app (and never needs to stop it).
/// </summary>
internal static class InstanceIdentity
{
    internal const string DefaultMutexName = "QuickLookNext.App.Mutex";
    internal const string PipePrefix = "QuickLookNext.App.Pipe.";

    private static readonly string Suffix = NormalizeSuffix(
        Environment.GetEnvironmentVariable("QL_INSTANCE_SUFFIX"));

    internal static string MutexName => MutexNameFor(Suffix);

    internal static string PipeName(string userSid) => PipeNameFor(userSid, Suffix);

    internal static string MutexNameFor(string suffix) => DefaultMutexName + suffix;

    internal static string PipeNameFor(string userSid, string suffix) =>
        PipePrefix + userSid + suffix;

    /// <summary>
    /// Turns the requested suffix into a name fragment that is safe in a mutex or
    /// pipe name. The leading dot keeps it readable; anything outside the allowed
    /// set is dropped so two builds cannot accidentally collide with a name that
    /// the pipe implementation would reject anyway.
    /// </summary>
    internal static string NormalizeSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = new StringBuilder(".", 33);
        foreach (var c in value.Trim())
        {
            if (text.Length >= 32)
                break;

            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                text.Append(c);
        }

        return text.Length == 1 ? string.Empty : text.ToString();
    }
}
