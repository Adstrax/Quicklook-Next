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
using System.Net;
using System.Threading.Tasks;

namespace QuickLook.Plugin.MarkdownViewer;

internal enum MarkdownPreviewGenerationState
{
    Success,
    Empty,
    Faulted,
    Canceled,
}

/// <summary>
/// Reads a background Markdown build without touching Result unless the task
/// succeeded. The failure is kept for the diagnostic log, while a small local
/// page lets the first-frame handoff complete immediately.
/// </summary>
internal readonly record struct MarkdownPreviewGenerationResult(
    MarkdownPreviewGenerationState State,
    string Html,
    Exception Error)
{
    internal bool IsSuccess => State == MarkdownPreviewGenerationState.Success;

    internal static MarkdownPreviewGenerationResult Read(Task<string> task)
    {
        if (task.IsCanceled)
            return new(
                MarkdownPreviewGenerationState.Canceled,
                null,
                new TaskCanceledException(task));

        if (task.IsFaulted)
            return new(
                MarkdownPreviewGenerationState.Faulted,
                null,
                task.Exception?.GetBaseException() ??
                new InvalidOperationException("Markdown rendering failed."));

        if (!task.IsCompletedSuccessfully)
            return new(
                MarkdownPreviewGenerationState.Faulted,
                null,
                new InvalidOperationException("Markdown rendering did not complete."));

        var html = task.Result;
        return string.IsNullOrWhiteSpace(html)
            ? new(
                MarkdownPreviewGenerationState.Empty,
                null,
                new InvalidOperationException("The Markdown renderer returned an empty page."))
            : new(MarkdownPreviewGenerationState.Success, html, null);
    }

    /// <summary>
    /// The page deliberately carries the same <c>preview</c> container used by
    /// the normal renderer. WebpagePanel waits for a child of that container, so
    /// the failure page now completes the handoff instead of waiting for the
    /// three-second fallback.
    /// </summary>
    internal static string ErrorHtml(string path)
    {
        var fileName = string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFileName(path);
        var safeName = WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(fileName) ? path ?? string.Empty : fileName);
        var fileLine = string.IsNullOrWhiteSpace(safeName)
            ? string.Empty
            : $"<p class=\"file\">{safeName}</p>";

        return
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
            "<meta name=\"color-scheme\" content=\"light dark\">" +
            "<style>" +
            "html,body{background:transparent!important}" +
            "body{margin:0;font-family:'Segoe UI',sans-serif}" +
            "#preview{min-height:100vh;display:flex;align-items:center;justify-content:center;" +
            "box-sizing:border-box;padding:32px}" +
            ".message{max-width:620px;color:#B42318;text-align:center}" +
            ".message h1{margin:0 0 8px;font-size:18px;font-weight:600}" +
            ".message p{margin:0;font-size:14px;line-height:1.6}" +
            ".message .file{margin-top:12px;font-weight:600;overflow-wrap:anywhere}" +
            "@media(prefers-color-scheme:dark){.message{color:#FFB4AB}}" +
            "</style></head><body><main id=\"preview\"><section class=\"message\">" +
            "<h1>Preview unavailable</h1>" +
            "<p>This document could not be read. It may be temporarily in use, corrupt, " +
            "or in an unsupported format. Details were written to the diagnostic log.</p>" +
            fileLine +
            "</section></main></body></html>";
    }
}
