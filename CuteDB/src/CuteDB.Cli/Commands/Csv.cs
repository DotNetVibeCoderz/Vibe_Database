using System.Globalization;
using System.Text;

namespace CuteDB.Cli.Commands;

/// <summary>
/// CSV, both directions, in one place so the writer and the reader agree on every rule.
/// </summary>
/// <remarks>
/// <para>
/// They used to live apart — the writer in <c>export</c> and <c>query</c>, the reader in
/// <c>import</c> — and had drifted: the writer quoted a cell containing a newline, which is right,
/// and the reader split records on lines, so that cell came back as two broken rows. A round-trip
/// test caught it on the first run.
/// </para>
/// <para>
/// What CSV can carry is decided here and documented in <c>docs/en/cli.md</c>. A cell is text, so
/// types come back by inference: numbers, booleans and ISO dates are recognised; nested objects and
/// arrays are written as JSON and read back as structure; <c>null</c> and missing both become an
/// empty cell and come back missing.
/// </para>
/// </remarks>
internal static class Csv
{
    /// <summary>The text a value is written as.</summary>
    internal static string Format(CuteValue value, CuteJsonOptions options) => value.Type switch
    {
        // A nested object or array has no CSV spelling, so it goes in as compact JSON rather than
        // as "{3 fields}" — the point of CSV output is that something else reads it, and import
        // reads it back as structure.
        CuteType.Object or CuteType.Array => CuteJson.Write(value, options),
        CuteType.Null or CuteType.Missing => string.Empty,

        // The round-trip ISO form rather than the display form: the display form drops the
        // fraction of a second and the zone, so a timestamp exported and imported came back
        // moved. ISO is also what spreadsheets and every date parser accept.
        CuteType.DateTime => value.AsDateTime.ToString("O", CultureInfo.InvariantCulture),
        _ => value.ToDisplayString(),
    };

    /// <summary>Quotes a cell when it has to be quoted, doubling any quote inside.</summary>
    /// <remarks>
    /// Leading or trailing spaces force quotes too: readers — this one included — trim an unquoted
    /// cell, so "  padded  " would otherwise come back as "padded".
    /// </remarks>
    internal static string Escape(string text)
    {
        var needsQuotes = text.AsSpan().IndexOfAny(",\"\n\r") >= 0
            || (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])));

        return needsQuotes
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;
    }

    /// <summary>Writes one row of cells.</summary>
    internal static void WriteRow(TextWriter writer, IEnumerable<string> cells)
        => writer.WriteLine(string.Join(',', cells.Select(Escape)));

    /// <summary>Reads CSV records, honouring quotes — including newlines inside quotes.</summary>
    /// <remarks>
    /// A record ends at a line break outside quotes, not at every line break, which is the
    /// difference between reading back a multi-line address and splitting it into two rows.
    /// Unquoted cells are trimmed, so hand-written <c>a, b</c> works; quoted cells are kept exactly.
    /// </remarks>
    internal static IEnumerable<List<string>> ReadRecords(TextReader reader)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var quoted = false;
        var any = false;

        void EndCell()
        {
            cells.Add(quoted ? cell.ToString() : cell.ToString().Trim());
            cell.Clear();
            quoted = false;
        }

        int next;
        while ((next = reader.Read()) >= 0)
        {
            var c = (char)next;
            any = true;

            if (inQuotes)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (reader.Peek() == '"')
                {
                    cell.Append('"');
                    reader.Read();
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    quoted = true;

                    // Anything before the opening quote on this cell was padding.
                    if (cell.ToString().Trim().Length == 0)
                    {
                        cell.Clear();
                    }

                    break;

                case ',':
                    EndCell();
                    break;

                case '\r':
                case '\n':
                    if (c == '\r' && reader.Peek() == '\n')
                    {
                        reader.Read();
                    }

                    EndCell();
                    if (!(cells.Count == 1 && cells[0].Length == 0))
                    {
                        yield return cells;
                    }

                    cells = [];
                    any = false;
                    break;

                // Space between a closing quote and the comma is layout, not content.
                case ' ' or '\t' when quoted:
                    break;

                default:
                    cell.Append(c);
                    break;
            }
        }

        if (any)
        {
            EndCell();
            if (!(cells.Count == 1 && cells[0].Length == 0))
            {
                yield return cells;
            }
        }
    }

    /// <summary>Turns a cell's text back into a typed value.</summary>
    /// <remarks>
    /// Type inference is the whole reason this is not just "everything is a string": a CSV of
    /// orders whose totals arrive as text cannot be summed, and asking people to cast in every
    /// query would be worse than guessing well here. The guesses are conservative where a wrong
    /// one destroys data: a number with a leading zero or a leading plus is a code — a phone
    /// number, a postcode — and stays text, because reading "0812…" as 812… cannot be undone.
    /// </remarks>
    internal static CuteValue Infer(string text, CuteJsonOptions options)
    {
        if (IsNumberSpelling(text))
        {
            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i32))
            {
                return CuteValue.Int32(i32);
            }

            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i64))
            {
                return CuteValue.Int64(i64);
            }

            if (options.PreferDecimal
                && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var dec))
            {
                return CuteValue.Decimal(dec);
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return CuteValue.Double(number);
            }
        }

        if (bool.TryParse(text, out var flag))
        {
            return CuteValue.Boolean(flag);
        }

        // Structure that export wrote as JSON. Only an object or array counts, and only if it
        // parses — "[draft] notes" is still a string.
        if (text.Length > 1 && text[0] is '{' or '[')
        {
            try
            {
                var parsed = CuteJson.Parse(text, options);
                if (parsed.IsObject || parsed.IsArray)
                {
                    return parsed;
                }
            }
            catch (CuteDbException)
            {
                // Not JSON; fall through to text.
            }
        }

        if (text.Length >= 8
            && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
        {
            return CuteValue.DateTime(date);
        }

        return CuteValue.String(text);
    }

    /// <summary>True when the text is written the way a number is: optional minus, no padding zero.</summary>
    private static bool IsNumberSpelling(string text)
    {
        var i = text.Length > 0 && text[0] == '-' ? 1 : 0;
        if (i >= text.Length || !char.IsAsciiDigit(text[i]))
        {
            return false;
        }

        return !(text[i] == '0' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]));
    }
}
