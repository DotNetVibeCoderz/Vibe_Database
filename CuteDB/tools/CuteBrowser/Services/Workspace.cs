using CuteDB.Query;

namespace CuteDB.Browser.Services;

/// <summary>What a collection looks like, worked out from the documents in it.</summary>
/// <param name="Name">The collection.</param>
/// <param name="Path">The field path, dotted for subdocuments.</param>
/// <param name="Types">The types seen at that path, most common first.</param>
/// <param name="Presence">The fraction of sampled documents that had it, 0 to 1.</param>
/// <param name="Example">One value seen there, rendered short.</param>
public sealed record FieldShape(string Name, string Path, IReadOnlyList<string> Types, double Presence, string Example)
{
    /// <summary>How the explorer labels it: the type, and whether it is always there.</summary>
    public string Summary => Presence >= 0.999
        ? string.Join(" | ", Types)
        : $"{string.Join(" | ", Types)} · {Presence:P0}";
}

/// <summary>
/// The open database, and everything the rest of the app asks it.
/// </summary>
/// <remarks>
/// <para>
/// One database is open at a time, which matches CuteDB itself: a file is owned by one process and
/// one <see cref="CuteDatabase"/> within it. Opening a second closes the first, and every part of
/// the UI is told through <see cref="Opened"/> and <see cref="Closed"/> rather than holding its own
/// reference — a stale reference to a disposed database is the one bug this class exists to make
/// impossible.
/// </para>
/// <para>
/// Schema inference deserves a word. CuteDB is schemaless, so a collection has no declared shape;
/// what the explorer shows is what a sample of documents actually contains. That is honest, but it
/// is a sample — a field that appears in one document out of a hundred thousand may not be listed,
/// and the presence percentage is of the sample, not of the collection.
/// </para>
/// </remarks>
public sealed class Workspace
{
    /// <summary>How many documents the shape of a collection is inferred from.</summary>
    public const int SchemaSampleSize = 200;

    private CuteDatabase? _database;

    /// <summary>Creates a workspace that logs to the given log.</summary>
    public Workspace(ActivityLog log) => Log = log;

    /// <summary>The running record of what happened.</summary>
    public ActivityLog Log { get; }

    /// <summary>The open database, or null.</summary>
    public CuteDatabase? Database => _database;

    /// <summary>The path of the open file, or null for an in-memory database or none.</summary>
    public string? Path { get; private set; }

    /// <summary>Whether a database is open.</summary>
    public bool IsOpen => _database is not null;

    /// <summary>What the title bar and status bar call the open database.</summary>
    public string DisplayName => Path is null
        ? _database is null ? "no database" : "in-memory"
        : System.IO.Path.GetFileName(Path);

    /// <summary>Raised after a database is opened or created.</summary>
    public event Action? Opened;

    /// <summary>Raised after the database is closed.</summary>
    public event Action? Closed;

    /// <summary>Raised when collections or indexes change, so the explorer can refresh.</summary>
    public event Action? SchemaChanged;

    /// <summary>The open database, or an explanation of why there is not one.</summary>
    public CuteDatabase Require()
        => _database ?? throw new InvalidOperationException(
            "No database is open. Use File ▸ Open Database, or File ▸ New Database.");

    /// <summary>Opens a file, closing whatever was open.</summary>
    public void Open(string path)
    {
        Close();

        _database = CuteDatabase.Open(path);
        Path = path;

        Log.Good("workspace", $"Opened {path}");

        if (_database.DiscardedBytesOnOpen > 0)
        {
            // Worth saying out loud rather than leaving in a property nobody reads: the previous
            // process was interrupted, and some of what it was writing did not survive.
            Log.Info(
                "workspace",
                $"Recovered from an interrupted write: {_database.DiscardedBytesOnOpen:N0} bytes of "
                + "incomplete tail discarded. Everything before that point is intact.");
        }

        BrowserSettings.Current.Remember(path);
        Opened?.Invoke();
        SchemaChanged?.Invoke();
    }

    /// <summary>Opens a database that lives only for as long as the app does.</summary>
    public void OpenInMemory()
    {
        Close();

        _database = CuteDatabase.CreateInMemory();
        Path = null;

        Log.Good("workspace", "Opened an in-memory database. Nothing here is written to disk.");
        Opened?.Invoke();
        SchemaChanged?.Invoke();
    }

    /// <summary>Closes the open database, if any.</summary>
    public void Close()
    {
        if (_database is null)
        {
            return;
        }

        var name = DisplayName;
        _database.Dispose();
        _database = null;
        Path = null;

        BrowserSettings.Current.LastDatabase = string.Empty;
        BrowserSettings.Current.Save();

        Log.Info("workspace", $"Closed {name}");
        Closed?.Invoke();
        SchemaChanged?.Invoke();
    }

    /// <summary>The collections in the open database, in name order.</summary>
    public IReadOnlyList<string> Collections()
        => _database is null ? [] : [.. _database.CollectionNames.OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>Creates a collection, which in CuteDB means naming one.</summary>
    public void AddCollection(string name)
    {
        var database = Require();

        if (database.CollectionNames.Contains(name, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"'{name}' already exists.");
        }

        database.Collection(name);
        Log.Good("workspace", $"Created collection '{name}'");
        SchemaChanged?.Invoke();
    }

    /// <summary>Deletes a collection and everything in it.</summary>
    public bool DropCollection(string name)
    {
        var dropped = Require().DropCollection(name);
        Log.Write(dropped ? LogLevel.Good : LogLevel.Info, "workspace",
            dropped ? $"Dropped collection '{name}'" : $"No collection named '{name}'");

        SchemaChanged?.Invoke();
        return dropped;
    }

    /// <summary>Copies a collection under a new name. CuteDB has no rename, so this is the whole of one.</summary>
    public int CopyCollection(string from, string to)
    {
        var database = Require();
        var source = database.TryGetCollection(from)
            ?? throw new InvalidOperationException($"No collection named '{from}'.");

        var target = database.Collection(to);
        var copied = target.InsertMany(source.All());

        Log.Good("workspace", $"Copied {copied:N0} documents from '{from}' to '{to}'");
        SchemaChanged?.Invoke();
        return copied;
    }

    /// <summary>Tells the explorer something changed underneath it.</summary>
    public void NotifySchemaChanged() => SchemaChanged?.Invoke();

    /// <summary>
    /// Works out what a collection's documents look like, by sampling them.
    /// </summary>
    /// <remarks>
    /// Nested objects are walked so <c>address.city</c> shows as its own row; arrays are reported
    /// as an array with their element type rather than expanded, because an explorer that unfolds
    /// every line of every order is unreadable. What the assistant is given is this same list.
    /// </remarks>
    public IReadOnlyList<FieldShape> Describe(string collection, int sample = SchemaSampleSize)
    {
        var database = Require();
        var target = database.TryGetCollection(collection);
        if (target is null)
        {
            return [];
        }

        var seen = new Dictionary<string, (List<string> Types, int Count, string Example)>(StringComparer.Ordinal);
        var documents = target.All().Take(sample).ToList();

        foreach (var document in documents)
        {
            Walk(document.Root, prefix: string.Empty, seen);
        }

        var total = Math.Max(documents.Count, 1);

        return
        [
            .. seen
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new FieldShape(
                    entry.Key.Split('.')[^1],
                    entry.Key,
                    entry.Value.Types,
                    (double)entry.Value.Count / total,
                    entry.Value.Example))
        ];
    }

    private static void Walk(
        CuteObject value,
        string prefix,
        Dictionary<string, (List<string> Types, int Count, string Example)> seen)
    {
        foreach (var (name, field) in value)
        {
            var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
            var type = Describe(field);

            if (!seen.TryGetValue(path, out var entry))
            {
                entry = ([], 0, Shorten(field));
                seen[path] = entry;
            }

            if (!entry.Types.Contains(type, StringComparer.Ordinal))
            {
                entry.Types.Add(type);
            }

            seen[path] = (entry.Types, entry.Count + 1, entry.Example);

            if (field.Type == CuteType.Object)
            {
                Walk(field.AsObject, path, seen);
            }
        }
    }

    private static string Describe(CuteValue value) => value.Type switch
    {
        CuteType.Array when value.AsArray.Count > 0 => $"array<{Describe(value.AsArray[0])}>",
        CuteType.Array => "array",
        CuteType.Object => "object",
        CuteType.Int32 or CuteType.Int64 => "int",
        CuteType.Double => "double",
        CuteType.Decimal => "decimal",
        CuteType.String => "string",
        CuteType.True or CuteType.False => "bool",
        CuteType.DateTime => "datetime",
        CuteType.Guid => "guid",
        CuteType.Id => "id",
        CuteType.Binary => "binary",
        CuteType.Null => "null",
        _ => value.Type.ToString().ToLowerInvariant(),
    };

    private static string Shorten(CuteValue value)
    {
        var text = value.Type switch
        {
            CuteType.Object => "{…}",
            CuteType.Array => $"[{value.AsArray.Count} items]",
            _ => value.ToDisplayString(),
        };

        return text.Length <= 40 ? text : text[..37] + "…";
    }
}
