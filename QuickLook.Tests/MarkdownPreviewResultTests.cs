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

using QuickLook.Plugin.MarkdownViewer;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace QuickLook.Tests;

internal class MarkdownPreviewResultTests
{
    public void ASuccessfulTaskYieldsTheRenderedPage()
    {
        var result = MarkdownPreviewGenerationResult.Read(Task.FromResult("<html>page</html>"));

        Assert.True(result.IsSuccess, "a completed render is usable");
        Assert.Equal("<html>page</html>", result.Html, "the rendered page is preserved");
        Assert.Null(result.Error, "success has no error");
    }

    public void AnEmptyPageIsReportedAsAFailure()
    {
        var result = MarkdownPreviewGenerationResult.Read(Task.FromResult("  "));

        Assert.Equal(MarkdownPreviewGenerationState.Empty, result.State, "blank HTML is not a page");
        Assert.NotNull(result.Error, "an empty result explains what happened");
    }

    public void AFaultedTaskExposesItsBaseException()
    {
        var original = new InvalidOperationException("template unavailable");
        var result = MarkdownPreviewGenerationResult.Read(Task.FromException<string>(original));

        Assert.Equal(MarkdownPreviewGenerationState.Faulted, result.State, "the render failed");
        Assert.True(ReferenceEquals(original, result.Error), "the diagnostic error is preserved");
    }

    public void ACanceledTaskDoesNotThrowWhenRead()
    {
        var canceled = Task.FromCanceled<string>(new CancellationToken(true));
        var result = MarkdownPreviewGenerationResult.Read(canceled);

        Assert.Equal(MarkdownPreviewGenerationState.Canceled, result.State, "cancellation is distinct");
        Assert.True(result.Error is TaskCanceledException, "cancellation has a matching error");
    }

    public void TheErrorPageCanCompleteTheFirstFrameHandoff()
    {
        var html = MarkdownPreviewGenerationResult.ErrorHtml(@"C:\docs\paper.md");

        Assert.True(html.Contains("<main id=\"preview\">", StringComparison.Ordinal),
            "the normal first-frame observer recognizes the error page");
        Assert.True(html.Contains("background:transparent!important", StringComparison.Ordinal),
            "the error page keeps the WCA backdrop visible");
    }

    public void TheErrorPageEscapesTheFileName()
    {
        var html = MarkdownPreviewGenerationResult.ErrorHtml(@"C:\docs\<script>.md");

        Assert.False(html.Contains("<script>", StringComparison.OrdinalIgnoreCase),
            "a file name cannot inject markup into the error page");
        Assert.True(html.Contains("&lt;script&gt;.md", StringComparison.Ordinal),
            "the file name remains visible after encoding");
    }
}
