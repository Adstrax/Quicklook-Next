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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace QuickLook.Plugin.XMindViewer;

public sealed class XMindTopic(string title)
{
    public string Title { get; } = title;
    public List<XMindTopic> Children { get; } = [];
}

public sealed record XMindSheet(string Title, XMindTopic RootTopic);

/// <summary>
/// Reads the ZIP container used by XMind 2020+ and XMind 8. New files carry
/// content.json; older files use content.xml. Keeping the two paths separate
/// avoids depending on proprietary XMind assemblies.
/// </summary>
public static class XMindParser
{
    private const int MaxDepth = 96;
    private const int MaxTopics = 50000;
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    public static IReadOnlyList<XMindSheet> Parse(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);

        Exception jsonError = null;
        var jsonEntry = FindEntry(archive, "content.json");
        if (jsonEntry != null)
        {
            try
            {
                using var stream = jsonEntry.Open();
                var sheets = ParseJson(stream, cancellationToken);
                if (sheets.Count > 0)
                    return sheets;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                jsonError = e;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var xmlEntry = FindEntry(archive, "content.xml");
        if (xmlEntry != null)
        {
            try
            {
                using var stream = xmlEntry.Open();
                var sheets = ParseXml(stream, cancellationToken);
                if (sheets.Count > 0)
                    return sheets;
            }
            catch (Exception xmlError) when (xmlError is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    "The XMind content could not be read from either content.json or content.xml.",
                    jsonError == null ? xmlError : new AggregateException(jsonError, xmlError));
            }
        }

        if (jsonError != null)
            throw new InvalidDataException("The XMind content.json is invalid.", jsonError);

        throw new InvalidDataException("The file is not a readable XMind document.");
    }

    internal static IReadOnlyList<XMindSheet> ParseJson(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(stream);
        var sheetElements = new List<JsonElement>();

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            sheetElements.AddRange(document.RootElement.EnumerateArray());
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(document.RootElement, "sheets", out var sheets) &&
                sheets.ValueKind == JsonValueKind.Array)
            {
                sheetElements.AddRange(sheets.EnumerateArray());
            }
            else
            {
                sheetElements.Add(document.RootElement);
            }
        }

        var result = new List<XMindSheet>();
        var topicCount = 0;
        for (var index = 0; index < sheetElements.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sheet = sheetElements[index];
            if (sheet.ValueKind != JsonValueKind.Object)
                continue;

            if (!TryGetProperty(sheet, "rootTopic", out var rootElement) &&
                !TryGetProperty(sheet, "root", out rootElement))
            {
                continue;
            }

            var root = ParseJsonTopic(rootElement, 0, ref topicCount, cancellationToken);
            if (root == null)
                continue;

            var sheetTitle = ReadText(sheet, "title");
            if (string.IsNullOrWhiteSpace(sheetTitle))
                sheetTitle = $"Sheet {index + 1}";
            result.Add(new XMindSheet(sheetTitle, root));
        }

        return result;
    }

    internal static IReadOnlyList<XMindSheet> ParseXml(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var document = XDocument.Load(stream, LoadOptions.None);
        var sheetElements = document.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "sheet", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sheetElements.Count == 0)
        {
            var rootTopic = document.Descendants()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, "topic", StringComparison.OrdinalIgnoreCase));
            if (rootTopic == null)
                return [];
            sheetElements.Add(new XElement("sheet", rootTopic));
        }

        var result = new List<XMindSheet>();
        var topicCount = 0;
        for (var index = 0; index < sheetElements.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sheet = sheetElements[index];
            var rootElement = sheet.DescendantsAndSelf().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, "topic", StringComparison.OrdinalIgnoreCase));
            if (rootElement == null)
                continue;

            var root = ParseXmlTopic(rootElement, 0, ref topicCount, cancellationToken);
            if (root == null)
                continue;

            var titleElement = sheet.Elements().FirstOrDefault(e =>
                string.Equals(e.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase));
            var sheetTitle = CleanText(titleElement?.Value);
            if (string.IsNullOrWhiteSpace(sheetTitle))
                sheetTitle = $"Sheet {index + 1}";
            result.Add(new XMindSheet(sheetTitle, root));
        }

        return result;
    }

    private static XMindTopic ParseJsonTopic(
        JsonElement element,
        int depth,
        ref int topicCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (element.ValueKind != JsonValueKind.Object ||
            depth > MaxDepth ||
            topicCount >= MaxTopics)
        {
            return null;
        }

        topicCount++;
        var topic = new XMindTopic(
            ReadText(element, "title") ??
            ReadText(element, "text") ??
            "Untitled topic");

        if (!TryGetProperty(element, "children", out var children) ||
            children.ValueKind != JsonValueKind.Object)
        {
            return topic;
        }

        foreach (var groupName in new[] { "attached", "detached", "callout", "summary" })
        {
            if (!TryGetProperty(children, groupName, out var group) ||
                group.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var childElement in group.EnumerateArray())
            {
                var child = ParseJsonTopic(childElement, depth + 1, ref topicCount, cancellationToken);
                if (child != null)
                    topic.Children.Add(child);
            }
        }

        return topic;
    }

    private static XMindTopic ParseXmlTopic(
        XElement element,
        int depth,
        ref int topicCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > MaxDepth || topicCount >= MaxTopics)
            return null;

        topicCount++;
        var titleElement = element.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, "title", StringComparison.OrdinalIgnoreCase));
        var topic = new XMindTopic(
            CleanText(titleElement?.Value) ??
            CleanText((string)element.Attribute("title")) ??
            "Untitled topic");

        foreach (var children in element.Elements().Where(e =>
                     string.Equals(e.Name.LocalName, "children", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var topics in children.Elements().Where(e =>
                         string.Equals(e.Name.LocalName, "topics", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var childElement in topics.Elements().Where(e =>
                             string.Equals(e.Name.LocalName, "topic", StringComparison.OrdinalIgnoreCase)))
                {
                    var child = ParseXmlTopic(childElement, depth + 1, ref topicCount, cancellationToken);
                    if (child != null)
                        topic.Children.Add(child);
                }
            }
        }

        return topic;
    }

    private static ZipArchiveEntry FindEntry(ZipArchive archive, string name)
    {
        return archive.Entries.FirstOrDefault(entry =>
            string.Equals(
                entry.FullName.Replace('\\', '/'),
                name,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string ReadText(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => CleanText(value.GetString()),
            JsonValueKind.Object when TryGetProperty(value, "content", out var content) =>
                content.ValueKind == JsonValueKind.String ? CleanText(content.GetString()) : null,
            JsonValueKind.Object when TryGetProperty(value, "text", out var text) =>
                text.ValueKind == JsonValueKind.String ? CleanText(text.GetString()) : null,
            _ => null,
        };
    }

    private static string CleanText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var decoded = WebUtility.HtmlDecode(HtmlTagRegex.Replace(value, " "));
        decoded = Regex.Replace(decoded, @"\s+", " ").Trim();
        return decoded.Length == 0 ? null : decoded;
    }
}
