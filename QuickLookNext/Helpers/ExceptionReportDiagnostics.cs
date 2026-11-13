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

using QuickLook.Common.Helpers;
using System;
using System.Globalization;

namespace QuickLookNext.Helpers;

internal static class ExceptionReportDiagnostics
{
    private const string LayoutFailureText =
        "Layout measurement override of element 'System.Windows.Controls.Canvas' should not return NaN values as its DesiredSize.";

    public static bool IsReportSurfaceFailure(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is InvalidOperationException &&
                current.Message.Contains(LayoutFailureText, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string BuildFallbackMessage(Exception exception)
    {
        var detail = exception is null
            ? "Unknown error"
            : $"{exception.GetType().Name}: {exception.Message}";

        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
                QuickLook-Next 遇到内部错误，内置错误报告窗口未能正常显示。

                {detail}

                详细信息已写入：
                {ProcessHelper.LogPath}
                """;
        }

        return $"""
            QuickLook-Next encountered an internal error and the built-in report window could not be displayed.

            {detail}

            Details were written to:
            {ProcessHelper.LogPath}
            """;
    }
}
