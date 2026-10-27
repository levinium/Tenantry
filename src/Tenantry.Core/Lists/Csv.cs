using System.Text;

namespace Tenantry.Core.Lists;

/// <summary>
/// Reads and writes CSV the way Excel does: commas (or semicolons, in countries that use them),
/// quotes around values that need them, doubled quotes inside, and line breaks inside quotes.
/// </summary>
public static class Csv
{
    /// <summary>Every row, as cells; blank lines are skipped. The separator is guessed from the first line.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Read(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];
        var firstLine = text.Split('\n', 2)[0];
        var separator = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0)
                quoted = true;
            else if (c == separator)
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Any(v => v.Trim().Length > 0))
                    rows.Add(row);
                row = [];
            }
            else
                cell.Append(c);
        }
        row.Add(cell.ToString());
        if (row.Any(v => v.Trim().Length > 0))
            rows.Add(row);
        return rows;
    }

    public static string Quote(string value) =>
        value.IndexOfAny([',', ';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    public static string Write(IEnumerable<IEnumerable<string>> rows) =>
        string.Join("\r\n", rows.Select(r => string.Join(',', r.Select(Quote)))) + "\r\n";
}
