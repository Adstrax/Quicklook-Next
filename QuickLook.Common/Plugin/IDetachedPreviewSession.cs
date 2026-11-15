// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Threading.Tasks;

namespace QuickLook.Common.Plugin;

/// <summary>
/// A complete preview window owned by the QuickLook host but shown beside a
/// long-lived preview such as an archive browser. It uses the same viewer,
/// plugin matching, WCA backdrop and size fitting as an ordinary preview.
/// </summary>
public interface IDetachedPreviewSession : IDisposable
{
    /// <summary>
    /// Shows the first entry or replaces the entry currently shown in the same window.
    /// </summary>
    Task ShowAsync(string path);

    /// <summary>
    /// Raised on the UI thread after the preview window has closed.
    /// </summary>
    event EventHandler Closed;
}
