using MiniExcelLibs;
using QuickLook.Common.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v3.12.0: self-rendered spreadsheet preview. The workbook is read with
/// MiniExcel and rendered as a styled HTML table in WebView2.
/// </summary>
public sealed class SpreadsheetPanel : OfficePanelBase
{
    private const int MaxRows = 300;
    private const int MaxColumns = 50;

    public SpreadsheetPanel(string path)
    {
        NavigateAsync(() => BuildHtml(path));
    }

    private static string BuildHtml(string path)
    {
        var rows = ReadRows(path);

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
        // v3.17.0: fixed light paper rendering - never theme-adapted.
        sb.Append(":root{--fg:#1A1A1A;--border:rgba(0,0,0,.12);" +
            "--head-bg:#F3F3F3;--alt-bg:rgba(0,0,0,.02);--error:#C42B1C;" +
            "--scroll-thumb:rgba(0,0,0,.28);--scroll-thumb-hover:rgba(0,0,0,.45)}");
        // v3.17.0 fix: opaque white surface - transparent WebView2 pages can
        // composite to black on some window setups.
        sb.Append("html,body{height:100%}body{margin:0;color:var(--fg);background:#FFFFFF;" +
            "font-family:'Segoe UI',Helvetica,Arial,sans-serif}");
        sb.Append("::-webkit-scrollbar{width:8px;height:8px}");
        sb.Append("::-webkit-scrollbar-track{background:transparent}");
        sb.Append("::-webkit-scrollbar-thumb{background:var(--scroll-thumb);border-radius:4px}");
        sb.Append("::-webkit-scrollbar-thumb:hover{background:var(--scroll-thumb-hover)}");
        sb.Append("::-webkit-scrollbar-corner{background:transparent}");
        sb.Append(".wrap{padding:16px;overflow:auto;height:100%;box-sizing:border-box}");
        sb.Append("table{border-collapse:collapse;font-size:13px;min-width:100%}");
        sb.Append("th,td{border:1px solid var(--border);padding:5px 10px;white-space:nowrap}");
        sb.Append("thead th{position:sticky;top:0;background:var(--head-bg);font-weight:600;z-index:1}");
        sb.Append("tbody tr:nth-child(even) td{background:var(--alt-bg)}");
        sb.Append(".error{color:var(--error);padding:24px;font-size:14px}");
        sb.Append("</style></head><body>");

        if (rows is null)
        {
            sb.Append("<div class=\"error\">This workbook cannot be read (the file may be corrupt or in an unsupported format).</div>");
        }
        else
        {
            sb.Append("<div class=\"wrap\"><table><thead><tr>");
            var colCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            for (var c = 0; c < colCount; c++)
                sb.Append("<th>").Append(ColumnName(c)).Append("</th>");
            sb.Append("</tr></thead><tbody>");
            foreach (var row in rows)
            {
                sb.Append("<tr>");
                foreach (var cell in row)
                    sb.Append("<td>").Append(cell).Append("</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table></div>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static List<List<string>> ReadRows(string path)
    {
        try
        {
            var result = new List<List<string>>();
            foreach (var row in MiniExcel.Query(path, useHeaderRow: false))
            {
                var cells = new List<string>();
                foreach (var kv in row)
                {
                    if (cells.Count >= MaxColumns)
                        break;
                    cells.Add(kv.Value is null ? string.Empty : WebUtility.HtmlEncode(kv.Value.ToString()));
                }

                while (cells.Count > 0 && cells[^1].Length == 0)
                    cells.RemoveAt(cells.Count - 1);

                result.Add(cells);
                if (result.Count >= MaxRows)
                    break;
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        var n = index;
        while (n >= 0)
        {
            name = (char)('A' + n % 26) + name;
            n = n / 26 - 1;
        }

        return name;
    }
}
