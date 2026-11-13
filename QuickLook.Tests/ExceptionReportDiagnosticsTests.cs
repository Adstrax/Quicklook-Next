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

using QuickLookNext.Helpers;
using System;
using System.IO;

namespace QuickLook.Tests;

internal class ExceptionReportDiagnosticsTests
{
    public void RecognizesTheExceptionReportCanvasFailure()
    {
        var exception = new InvalidOperationException(
            "Layout measurement override of element 'System.Windows.Controls.Canvas' " +
            "should not return NaN values as its DesiredSize.");

        Assert.True(ExceptionReportDiagnostics.IsReportSurfaceFailure(exception),
            "the known report-window layout failure is recognized");
    }

    public void RecognizesAWrappedReportFailure()
    {
        var exception = new AggregateException(
            new InvalidOperationException(
                "Layout measurement override of element 'System.Windows.Controls.Canvas' " +
                "should not return NaN values as its DesiredSize."));

        Assert.True(ExceptionReportDiagnostics.IsReportSurfaceFailure(exception),
            "the failure is still recognized through an inner exception");
    }

    public void DoesNotTreatAnOrdinaryPreviewErrorAsAReportFailure()
    {
        var exception = new FileNotFoundException("temporary preview file disappeared");

        Assert.False(ExceptionReportDiagnostics.IsReportSurfaceFailure(exception),
            "ordinary preview exceptions do not close the report window");
    }

    public void FallbackMessageIncludesTheErrorAndDiagnosticLog()
    {
        var exception = new InvalidOperationException("layout failed");
        var message = ExceptionReportDiagnostics.BuildFallbackMessage(exception);

        Assert.True(message.Contains("InvalidOperationException", StringComparison.Ordinal),
            "the fallback identifies the exception type");
        Assert.True(message.Contains("layout failed", StringComparison.Ordinal),
            "the fallback keeps the exception message");
        Assert.True(message.Contains("QuickLookNext.Exception.log", StringComparison.Ordinal),
            "the fallback points to the diagnostic log");
    }
}
