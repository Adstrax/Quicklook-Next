// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLookNext program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Xml.Linq;
using MiniExcelLibs;

namespace QuickLook.Plugin.OfficeViewer;

/// <summary>
/// v3.13.0: renders a workbook as a paper-like table.
/// <para>
/// v5.6.19: three things the preview used to hide:
/// </para>
/// <list type="bullet">
/// <item><description>the worksheets. Only the first one was ever shown; the rail lists all of them (the reason
/// it is usually empty is that most workbooks have one sheet, and then there is nothing to choose);</description></item>
/// <item><description>the rows. The page always showed rows 1-300 and said nothing about it - one test workbook
/// has 66,353 of them. The footer now reports the range and the total, and walks or jumps through the
/// file;</description></item>
/// <item><description>the sheet's name and row count are read straight from the package (the workbook part and
/// each worksheet's dimension), so no cell has to be parsed to know what is there.</description></item>
/// </list>
/// </summary>
public sealed class SpreadsheetPanel : OfficePanelBase
{
    private const int WindowRows = 300;
    private const int MaxColumns = 50;

    private readonly string _path;
    private readonly List<SheetInfo> _sheets;
    private string _sheet;
    private int _start;

    public SpreadsheetPanel(string path)
    {
        _path = path;
        _sheets = ReadSheets(path);
        _sheet = _sheets.Count > 0 ? _sheets[0].Name : null;
        Render();
    }

    /// <summary>One worksheet: what it is called and how many rows it has.</summary>
    private sealed record SheetInfo(string Name, int RowCount);

    private void Render()
    {
        var path = _path;
        var sheet = _sheet;
        var start = _start;
        var sheets = _sheets;

        NavigateAsync(() =>
        {
            var rows = ReadRows(path, sheet, start, out var error);
            var total = sheets.FirstOrDefault(s => string.Equals(s.Name, sheet, StringComparison.OrdinalIgnoreCase))
                ?.RowCount ?? 0;

            var navigation = sheets.Count > 1
                ? sheets.Select(s => new OfficeNavEntry(
                    s.RowCount > 0 ? $"{s.Name}  ({s.RowCount:N0} rows)" : s.Name, s.Name)).ToList()
                : [];
            var selected = sheets.FindIndex(s => string.Equals(s.Name, sheet, StringComparison.OrdinalIgnoreCase));

            return new OfficePanelContent
            {
                Html = BuildHtml(sheet, rows, error, start, total),
                Navigation = navigation,
                SelectedIndex = selected < 0 ? 0 : selected,
            };
        });
    }

    protected override void OnNavigationSelected(int index)
    {
        if (index < 0 || index >= _sheets.Count)
            return;

        _sheet = _sheets[index].Name;
        _start = 0;
        Render();
    }

    /// <summary>The page's own pager posts here: {"action":"rows","start":N}.</summary>
    protected override void OnWebMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            using var document = JsonDocument.Parse(message);
            if (!document.RootElement.TryGetProperty("action", out var action) ||
                action.GetString() != "rows" ||
                !document.RootElement.TryGetProperty("start", out var startValue))
                return;

            var start = Math.Max(0, startValue.GetInt32());
            if (start == _start)
                return;

            _start = start;
            Render();
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
        }
    }

    private static string BuildHtml(
        string sheet, List<List<string>> rows, string error, int start, int total)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>");
        // v3.17.0: fixed light paper rendering - never theme-adapted.
        sb.Append(":root{--fg:#1A1A1A;--border:rgba(0,0,0,.12);--muted:#6B6B6B;" +
            "--head-bg:#F3F3F3;--alt-bg:rgba(0,0,0,.02);--error:#C42B1C;" +
            "--scroll-thumb:rgba(0,0,0,.28);--scroll-thumb-hover:rgba(0,0,0,.45)}");
        // v3.17.0 fix: opaque white surface - transparent WebView2 pages can
        // composite to black on some window setups.
        sb.Append("html,body{height:100%}body{margin:0;color:var(--fg);background:#FFFFFF;" +
            "font-family:'Segoe UI',Helvetica,Arial,sans-serif;display:flex;flex-direction:column}");
        sb.Append("::-webkit-scrollbar{width:8px;height:8px}");
        sb.Append("::-webkit-scrollbar-track{background:transparent}");
        sb.Append("::-webkit-scrollbar-thumb{background:var(--scroll-thumb);border-radius:4px}");
        sb.Append("::-webkit-scrollbar-thumb:hover{background:var(--scroll-thumb-hover)}");
        sb.Append("::-webkit-scrollbar-corner{background:transparent}");
        sb.Append(".head{flex:0 0 auto;padding:10px 16px 0;font-size:13px;color:var(--muted)}");
        sb.Append(".head b{color:var(--fg);font-weight:600}");
        sb.Append(".wrap{padding:12px 16px;overflow:auto;flex:1 1 auto;box-sizing:border-box}");
        sb.Append("table{border-collapse:collapse;font-size:13px;min-width:100%}");
        sb.Append("th,td{border:1px solid var(--border);padding:5px 10px;white-space:nowrap}");
        sb.Append("thead th{position:sticky;top:0;background:var(--head-bg);font-weight:600;z-index:1}");
        sb.Append("tbody tr:nth-child(even) td{background:var(--alt-bg)}");
        sb.Append(".rowno{position:sticky;left:0;background:var(--head-bg);color:var(--muted);" +
            "text-align:right;font-variant-numeric:tabular-nums}");
        sb.Append(".error{color:var(--error);padding:24px;font-size:14px}");
        sb.Append(".pager{flex:0 0 auto;display:flex;align-items:center;gap:6px;padding:8px 16px;" +
            "border-top:1px solid var(--border);font-size:12.5px;color:var(--muted);background:#FAFAFA}");
        sb.Append(".pager button{border:1px solid var(--border);background:#FFFFFF;color:var(--fg);" +
            "border-radius:4px;padding:3px 9px;font-size:12.5px;cursor:pointer}");
        sb.Append(".pager button:disabled{opacity:.4;cursor:default}");
        sb.Append(".pager input{width:88px;border:1px solid var(--border);border-radius:4px;" +
            "padding:3px 6px;font-size:12.5px;background:#FFFFFF;color:var(--fg)}");
        sb.Append("</style></head><body>");

        if (rows is null)
        {
            sb.Append("<div class=\"error\">This workbook cannot be read");
            if (!string.IsNullOrEmpty(error))
                sb.Append(" (").Append(WebUtility.HtmlEncode(error)).Append(')');
            sb.Append(".</div></body></html>");
            return sb.ToString();
        }

        var first = rows.Count == 0 ? 0 : start + 1;
        var last = start + rows.Count;

        // v5.6.19: say what is on screen - the sheet, the range and the size of the file behind it.
        sb.Append("<div class=\"head\">");
        if (!string.IsNullOrEmpty(sheet))
            sb.Append("<b>").Append(WebUtility.HtmlEncode(sheet)).Append("</b>");
        if (total > 0)
            sb.Append(" · rows ").Append(first.ToString("N0")).Append('–').Append(last.ToString("N0"))
              .Append(" of ").Append(total.ToString("N0"));
        else
            sb.Append(" · ").Append(rows.Count.ToString("N0")).Append(rows.Count == 1 ? " row" : " rows");
        sb.Append("</div>");

        sb.Append("<div class=\"wrap\"><table><thead><tr><th class=\"rowno\"></th>");
        var colCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        for (var c = 0; c < colCount; c++)
            sb.Append("<th>").Append(ColumnName(c)).Append("</th>");
        sb.Append("</tr></thead><tbody>");

        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append("<tr><td class=\"rowno\">").Append((start + r + 1).ToString("N0")).Append("</td>");
            foreach (var cell in rows[r])
                sb.Append("<td>").Append(cell).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>");

        AppendPager(sb, start, rows.Count, total);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>
    /// v5.6.19: the pager talks to the panel over WebView2's own message channel; the panel answers by
    /// rendering the window that was asked for.
    /// </summary>
    private static void AppendPager(StringBuilder sb, int start, int count, int total)
    {
        var step = WindowRows;
        var lastStart = total > 0 ? Math.Max(0, ((total - 1) / step) * step) : 0;
        var previous = Math.Max(0, start - step);
        var next = total > 0 ? Math.Min(lastStart, start + step) : start + step;

        sb.Append("<div class=\"pager\">");
        sb.Append($"<button onclick=\"go(0)\" {(start <= 0 ? "disabled" : string.Empty)}>|&lt;</button>");
        sb.Append($"<button onclick=\"go({previous})\" {(start <= 0 ? "disabled" : string.Empty)}>&lt;</button>");
        sb.Append($"<button onclick=\"go({next})\" {(total > 0 && next <= start ? "disabled" : string.Empty)}>&gt;</button>");
        if (total > 0)
            sb.Append($"<button onclick=\"go({lastStart})\" {(start >= lastStart ? "disabled" : string.Empty)}>&gt;|</button>");
        sb.Append("<span>go to row</span><input id=\"rowno\" type=\"number\" min=\"1\" " +
            (total > 0 ? $"max=\"{total}\"" : string.Empty) + ">");
        sb.Append("<button onclick=\"goTo()\">Go</button>");
        sb.Append("</div>");

        sb.Append("<script>");
        sb.Append($"var _step={step};");
        sb.Append("function send(start){chrome.webview.postMessage(JSON.stringify({action:'rows',start:start}));}");
        sb.Append($"function go(start){{if(start<0)start=0;send(start);}}");
        sb.Append("function goTo(){var v=parseInt(document.getElementById('rowno').value,10);" +
            "if(isNaN(v)||v<1)return;send(Math.floor((v-1)/_step)*_step);}");
        sb.Append("document.getElementById('rowno').addEventListener('keydown',e=>{if(e.key==='Enter')goTo();});");
        sb.Append("</script>");
    }

    private static List<List<string>> ReadRows(string path, string sheet, int start, out string error)
    {
        error = null;
        try
        {
            var result = new List<List<string>>();
            var query = string.IsNullOrEmpty(sheet)
                ? MiniExcel.Query(path, useHeaderRow: false)
                : MiniExcel.Query(path, useHeaderRow: false, sheetName: sheet);

            foreach (var row in query.Skip(start).Take(WindowRows))
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
            }

            return result;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            error = e.Message;
            return null;
        }
    }

    /// <summary>
    /// v5.6.19: the worksheets in the order the workbook lists them, each with the row count from its
    /// <c>dimension</c> element - which sits at the very start of the worksheet part, so nothing else has to
    /// be parsed (a test workbook is 25 MB).
    /// </summary>
    private static List<SheetInfo> ReadSheets(string path)
    {
        var sheets = new List<SheetInfo>();
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var workbook = archive.GetEntry("xl/workbook.xml");
            if (workbook is null)
                return sheets;

            XDocument document;
            using (var stream = workbook.Open())
                document = XDocument.Load(stream);

            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var relationships = ReadRelationships(archive);

            foreach (var sheet in document.Root?.Element(main + "sheets")?.Elements(main + "sheet") ?? [])
            {
                var name = (string)sheet.Attribute("name");
                var id = (string)sheet.Attribute(rel + "id");
                if (string.IsNullOrEmpty(name) || id is null || !relationships.TryGetValue(id, out var target))
                    continue;

                sheets.Add(new SheetInfo(name, RowCount(archive, "xl/" + target.TrimStart('/'))));
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
        }

        return sheets;
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive archive)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (entry is null)
            return map;

        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        foreach (var relationship in document.Root?.Elements(rel + "Relationship") ?? [])
        {
            var id = (string)relationship.Attribute("Id");
            var target = (string)relationship.Attribute("Target");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(target))
                map[id] = target;
        }

        return map;
    }

    /// <summary>The end row of a worksheet's dimension, read from the head of the part only.</summary>
    private static int RowCount(ZipArchive archive, string sheetPath)
    {
        try
        {
            var entry = archive.GetEntry(sheetPath);
            if (entry is null)
                return 0;

            using var stream = entry.Open();
            var buffer = new byte[4096];
            var read = stream.Read(buffer, 0, buffer.Length);
            var head = Encoding.UTF8.GetString(buffer, 0, read);
            var match = Regex.Match(head, "dimension[^>]*ref=\"[A-Za-z]+\\d+:[A-Za-z]+(\\d+)\"");
            return match.Success && int.TryParse(match.Groups[1].Value, out var rows) ? rows : 0;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e.Message);
            return 0;
        }
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        for (var i = index; i >= 0; i = i / 26 - 1)
            name = (char)('A' + i % 26) + name;
        return name;
    }
}
