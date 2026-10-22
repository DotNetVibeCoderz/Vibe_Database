using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CuteDB.Query;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CuteDB.Cli.Commands;

/// <summary>Loads documents from a file into a collection.</summary>
/// <remarks>
/// JSON Lines is the format to prefer for anything large: it streams a document at a time, so
/// importing a file bigger than memory works, whereas a single JSON array has to be parsed whole
/// before the first document can be stored.
/// </remarks>
internal sealed class ImportCommand(IAnsiConsole console) : DatabaseCommand<ImportCommand.Settings>(console)
{
    internal sealed class Settings : DatabaseSettings
    {
        [CommandArgument(1, "<file>")]
        [Description("The file to read.")]
        public string File { get; init; } = string.Empty;

        [CommandOption("-c|--collection <NAME>")]
        [Description("Target collection. Defaults to the file name without its extension.")]
        public string? Collection { get; init; }

        [CommandOption("-f|--format <FORMAT>")]
        [Description("json, jsonl or csv. Inferred from the extension when omitted.")]
        public string? Format { get; init; }

        [CommandOption("--decimal")]
        [Description("Read fractional numbers as exact decimals rather than doubles. Use for money.")]
        public bool Decimal { get; init; }

        [CommandOption("--batch <SIZE>")]
        [Description("Documents per write batch. Default 10000.")]
        public int Batch { get; init; } = 10_000;
    }

    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, Settings settings)
    {
        if (!File.Exists(settings.File))
        {
            throw new CuteDbException($"There is no file at '{settings.File}'.");
        }

        var name = settings.Collection ?? Path.GetFileNameWithoutExtension(settings.File);
        var format = settings.Format ?? InferFormat(settings.File);
        var options = settings.Decimal ? CuteJsonOptions.Financial : CuteJsonOptions.Default;
        var collection = database.Collection(name);

        var timer = Stopwatch.StartNew();
        var imported = 0;

        console.Status()
            .Spinner(Spinner.Known.Line)
            .SpinnerStyle(new Style(foreground: Color.FromHex(Theme.Gold)))
            .Start($"[{Theme.Ink}]Membaca {Path.GetFileName(settings.File).EscapeMarkup()}…[/]", status =>
            {
                foreach (var batch in ReadBatches(settings, format, options))
                {
                    imported += collection.InsertMany(batch);
                    status.Status($"[{Theme.Ink}]{imported:N0} dokumen…[/]");
                }
            });

        database.Flush(durable: true);
        timer.Stop();

        Theme.WriteSuccess(
            console,
            $"{imported:N0} dokumen ke '{name}' dalam {Theme.FormatDuration(timer.Elapsed)} " +
            $"({imported / Math.Max(timer.Elapsed.TotalSeconds, 0.001):N0}/detik)");

        if (!settings.Decimal && format != "csv")
        {
            Theme.WriteNote(
                console,
                "Fractional numbers were read as doubles. Pass --decimal if this file holds money.");
        }

        return 0;
    }

    private static IEnumerable<List<CuteDocument>> ReadBatches(Settings settings, string format, CuteJsonOptions options)
    {
        var batch = new List<CuteDocument>(settings.Batch);

        foreach (var document in ReadDocuments(settings, format, options))
        {
            batch.Add(document);
            if (batch.Count >= settings.Batch)
            {
                yield return batch;
                batch = new List<CuteDocument>(settings.Batch);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static IEnumerable<CuteDocument> ReadDocuments(Settings settings, string format, CuteJsonOptions options)
    {
        switch (format)
        {
            case "jsonl":
                foreach (var line in File.ReadLines(settings.File))
                {
                    if (line.Trim().Length == 0)
                    {
                        continue;
                    }

                    var value = CuteJson.Parse(line, options);
                    yield return value.IsObject
                        ? WithId(new CuteDocument(value.AsObject, assignId: false))
                        : throw new CuteDbException($"Every line must be a JSON object; found {value.Type.ToDisplayName()}.");
                }

                break;

            case "json":
            {
                var parsed = CuteJson.Parse(File.ReadAllText(settings.File), options);

                // Both shapes are accepted: an array of documents, or a single document.
                if (parsed.IsArray)
                {
                    foreach (var item in parsed.AsArray.AsSpan().ToArray())
                    {
                        yield return item.IsObject
                            ? WithId(new CuteDocument(item.AsObject, assignId: false))
                            : throw new CuteDbException($"Array elements must be objects; found {item.Type.ToDisplayName()}.");
                    }
                }
                else if (parsed.IsObject)
                {
                    yield return WithId(new CuteDocument(parsed.AsObject, assignId: false));
                }
                else
                {
                    throw new CuteDbException($"Expected an object or an array of objects, found {parsed.Type.ToDisplayName()}.");
                }

                break;
            }

            case "csv":
                foreach (var document in ReadCsv(settings.File, options))
                {
                    yield return document;
                }

                break;

            default:
                throw new CuteDbException($"'{format}' is not an import format. Use json, jsonl or csv.");
        }
    }

    /// <summary>
    /// Reads a CSV file, using the header row as field names and inferring each cell's type.
    /// </summary>
    /// <remarks>
    /// Empty cells become missing fields rather than empty strings, which is what makes
    /// <c>IS MISSING</c> work on imported data. The rules for the rest are in <see cref="Csv"/>.
    /// </remarks>
    private static IEnumerable<CuteDocument> ReadCsv(string path, CuteJsonOptions options)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        List<string>? headers = null;

        foreach (var cells in Csv.ReadRecords(reader))
        {
            if (headers is null)
            {
                headers = cells;
                continue;
            }

            // With no _id column the fresh id goes first, where every other document keeps it.
            var document = headers.Contains(CuteDocument.IdField)
                ? new CuteDocument(new CuteObject(), assignId: false)
                : new CuteDocument();

            for (var i = 0; i < headers.Count && i < cells.Count; i++)
            {
                if (cells[i].Length == 0)
                {
                    continue;
                }

                // The id column is read as an id before inference sees it: an id that happens to
                // be all decimal digits — rare per row, but not over a million rows — would
                // otherwise be read as a number and replaced with a fresh one.
                document.Set(headers[i], headers[i] == CuteDocument.IdField && CuteId.TryParse(cells[i], out var id)
                    ? CuteValue.Id(id)
                    : Csv.Infer(cells[i], options));
            }

            yield return WithId(document);
        }
    }

    /// <summary>Turns an <c>_id</c> that arrived as text back into an id.</summary>
    /// <remarks>
    /// Plain JSON and CSV have no id type, so export writes an id as its 24-character hex string.
    /// The engine accepts that spelling as the document's key, but stores the field as it came —
    /// a string — so a restored document no longer had the id-typed <c>_id</c> it was exported
    /// with. Converting here makes a plain export and import put back what was taken out. A
    /// document with no usable <c>_id</c> is given a fresh one.
    /// </remarks>
    private static CuteDocument WithId(CuteDocument document)
    {
        var id = document[CuteDocument.IdField];
        if (id.Type == CuteType.String && CuteId.TryParse(id.AsString, out var parsed))
        {
            document[CuteDocument.IdField] = CuteValue.Id(parsed);
            return document;
        }

        return id.Type == CuteType.Id ? document : new CuteDocument(document.Root);
    }

    private static string InferFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jsonl" or ".ndjson" => "jsonl",
        ".csv" => "csv",
        ".json" => "json",
        var other => throw new CuteDbException(
            $"Cannot tell the format from '{other}'. Pass --format json, jsonl or csv."),
    };
}

/// <summary>Writes a collection out to a file or to standard output.</summary>
internal sealed class ExportCommand(IAnsiConsole console) : DatabaseCommand<ExportCommand.Settings>(console)
{
    internal sealed class Settings : DatabaseSettings
    {
        [CommandArgument(1, "<collection>")]
        [Description("The collection to export.")]
        public string Collection { get; init; } = string.Empty;

        [CommandOption("-o|--out <FILE>")]
        [Description("Write to this file. Prints to standard output when omitted.")]
        public string? Out { get; init; }

        [CommandOption("-f|--format <FORMAT>")]
        [Description("json, jsonl or csv. Inferred from --out when omitted, else jsonl.")]
        public string? Format { get; init; }

        [CommandOption("-w|--where <FILTER>")]
        [Description("Export only documents matching this CuteQL filter.")]
        public string? Where { get; init; }

        [CommandOption("--lossless")]
        [Description("Write dates, GUIDs, decimals and binaries in their tagged form so a round trip loses nothing.")]
        public bool Lossless { get; init; }
    }

    /// <inheritdoc />
    protected override bool WritesData(Settings settings) => settings.Out is null;

    /// <inheritdoc />
    protected override bool OnlyReads(Settings settings) => true;

    /// <inheritdoc />
    protected override int Run(IAnsiConsole console, CuteDatabase database, Settings settings)
    {
        var collection = database.TryGetCollection(settings.Collection)
            ?? throw new CuteDbException(
                $"There is no collection called '{settings.Collection}'. " +
                $"Existing: {string.Join(", ", database.CollectionNames)}.");

        var documents = settings.Where is null
            ? collection.All()
            : collection.Find(settings.Where);

        var format = settings.Format ?? (settings.Out is null ? "jsonl" : InferFormat(settings.Out));
        var options = settings.Lossless ? CuteJsonOptions.Lossless : CuteJsonOptions.Default;

        if (settings.Out is null)
        {
            // Standard output is flushed, never disposed: it belongs to the process, and closing
            // it would silence everything written after the export — including the error panel
            // of whatever runs next in the same process.
            Write(Console.Out, documents, format, options);
            Console.Out.Flush();
            return 0;
        }

        int written;
        using (var writer = new StreamWriter(settings.Out, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            written = Write(writer, documents, format, options);
        }

        Theme.WriteSuccess(console, $"{written:N0} dokumen → {settings.Out}");
        return 0;
    }

    private static int Write(TextWriter writer, IReadOnlyList<CuteDocument> documents, string format, CuteJsonOptions options)
    {
        switch (format)
        {
            case "jsonl":
                foreach (var document in documents)
                {
                    writer.WriteLine(CuteJson.Write(document.AsValue(), options));
                }

                return documents.Count;

            case "json":
            {
                // Written incrementally rather than by building one giant string: a million
                // documents is a file, not a value to hold in memory twice.
                writer.WriteLine("[");
                for (var i = 0; i < documents.Count; i++)
                {
                    writer.Write("  ");
                    writer.Write(CuteJson.Write(documents[i].AsValue(), options));
                    writer.WriteLine(i == documents.Count - 1 ? string.Empty : ",");
                }

                writer.WriteLine("]");
                return documents.Count;
            }

            case "csv":
            {
                // The column set is the union of every document's top-level fields, because a
                // collection has no schema to ask.
                var columns = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var document in documents)
                {
                    foreach (var key in document.Root.Keys)
                    {
                        if (seen.Add(key))
                        {
                            columns.Add(key);
                        }
                    }
                }

                Csv.WriteRow(writer, columns);

                foreach (var document in documents)
                {
                    Csv.WriteRow(writer, columns.Select(column => Csv.Format(document[column], options)));
                }

                return documents.Count;
            }

            default:
                throw new CuteDbException($"'{format}' is not an export format. Use json, jsonl or csv.");
        }
    }

    private static string InferFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jsonl" or ".ndjson" => "jsonl",
        ".csv" => "csv",
        _ => "json",
    };
}
