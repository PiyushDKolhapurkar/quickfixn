using System.Text;

namespace BirthdayBot;

/// <summary>Minimal RFC 4180 CSV reader/writer (quoted fields, embedded commas, quotes and newlines).</summary>
internal static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = new List<string>(); }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        rows.RemoveAll(r => r.Count == 1 && r[0].Length == 0);
        return rows;
    }

    /// <summary>Parses CSV with a header row into dictionaries keyed by header name.</summary>
    public static List<Dictionary<string, string>> ParseWithHeader(string text)
    {
        var rows = Parse(text.TrimStart('﻿'));
        if (rows.Count == 0)
            return new();
        var header = rows[0];
        return rows.Skip(1).Select(r =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
                d[header[i].Trim()] = i < r.Count ? r[i] : "";
            return d;
        }).ToList();
    }

    public static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    public static string FormatRow(IEnumerable<string> values) => string.Join(',', values.Select(Escape));
}
