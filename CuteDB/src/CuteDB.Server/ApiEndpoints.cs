using System.Text;
using CuteDB.Query;

namespace CuteDB.Server;

/// <summary>
/// The HTTP surface: collections, documents, queries and statistics.
/// </summary>
/// <remarks>
/// <para>
/// Requests and responses are CuteDB's own JSON, written and parsed by <see cref="CuteJson"/>
/// rather than by the framework's serialiser. That matters because a document is schemaless: there
/// is no CLR type to bind it to, and round-tripping it through a generic serialiser would flatten
/// exactly the type information — decimals, dates, ids — the database exists to keep.
/// </para>
/// <para>
/// Every response is written straight to the body stream as UTF-8. A collection of a million
/// documents is a stream, not a string to build in memory first.
/// </para>
/// </remarks>
public static class ApiEndpoints
{
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>Registers every route.</summary>
    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Json(new
        {
            status = "ok",
            engine = CuteDatabase.EngineDescription,
        }));

        app.MapGet("/openapi.json", (HttpContext context) => WriteRaw(context, OpenApi.Document));

        // --- collections -------------------------------------------------------------------

        app.MapGet("/v1/collections", (HttpContext context, CuteDatabase database) =>
        {
            var array = new CuteArray();
            foreach (var name in database.CollectionNames)
            {
                var stats = database.Collection(name).Stats();
                array.Add(CuteValue.Object(new CuteObject()
                    .Set("name", stats.Name)
                    .Set("documents", stats.DocumentCount)
                    .Set("indexes", stats.IndexCount)
                    .Set("liveBytes", stats.LiveBytes)
                    .Set("averageDocumentBytes", Math.Round(stats.AverageDocumentBytes, 1))));
            }

            return WriteValue(context, CuteValue.Array(array));
        });

        app.MapGet("/v1/collections/{collection}", (HttpContext context, CuteDatabase database, string collection) =>
        {
            var stats = Require(database, collection).Stats();
            var indexes = new CuteArray();
            foreach (var index in Require(database, collection).Indexes)
            {
                indexes.Add(CuteValue.Object(new CuteObject()
                    .Set("name", index.Name)
                    .Set("path", index.Path)
                    .Set("unique", index.Unique)
                    .Set("keys", index.KeyCount)
                    .Set("entries", index.EntryCount)));
            }

            return WriteValue(context, CuteValue.Object(new CuteObject()
                .Set("name", stats.Name)
                .Set("documents", stats.DocumentCount)
                .Set("liveBytes", stats.LiveBytes)
                .Set("deadBytes", stats.DeadBytes)
                .Set("reservedBytes", stats.ReservedBytes)
                .Set("averageDocumentBytes", Math.Round(stats.AverageDocumentBytes, 1))
                .Set("indexes", CuteValue.Array(indexes))));
        });

        app.MapDelete("/v1/collections/{collection}", (CuteDatabase database, string collection)
            => database.DropCollection(collection)
                ? Results.Json(new { dropped = collection })
                : Results.NotFound(new { error = "not_found", message = $"There is no collection called '{collection}'." }));

        // --- documents ---------------------------------------------------------------------

        app.MapGet("/v1/collections/{collection}/documents", async (
            HttpContext context,
            CuteDatabase database,
            string collection,
            string? filter,
            int? limit,
            int? offset) =>
        {
            // Paging is expressed as a CuteQL statement rather than implemented separately, so
            // there is exactly one code path deciding what "the next page" means.
            var query = new StringBuilder("SELECT * FROM ").Append(Quote(collection));
            if (!string.IsNullOrWhiteSpace(filter))
            {
                query.Append(" WHERE ").Append(filter);
            }

            query.Append(" LIMIT ").Append(Math.Clamp(limit ?? 100, 1, 10_000));
            if (offset is > 0)
            {
                query.Append(" OFFSET ").Append(offset.Value);
            }

            var result = database.Execute(query.ToString(), ReadParameters(context));
            await WriteResult(context, result);
        });

        app.MapGet("/v1/collections/{collection}/documents/{id}", async (
            HttpContext context,
            CuteDatabase database,
            string collection,
            string id) =>
        {
            if (!CuteId.TryParse(id, out var parsed))
            {
                await WriteValue(context, Error("invalid_id", $"'{id}' is not a document id."), 400);
                return;
            }

            var document = Require(database, collection).FindById(parsed);
            if (document is null)
            {
                await WriteValue(context, Error("not_found", $"No document {id} in '{collection}'."), 404);
                return;
            }

            await WriteValue(context, document.AsValue());
        });

        app.MapPost("/v1/collections/{collection}/documents", async (
            HttpContext context,
            CuteDatabase database,
            string collection) =>
        {
            var body = await ReadBody(context);
            var target = database.Collection(collection);

            // One object inserts one document; an array inserts many under a single lock. Callers
            // batching a load should send an array — it is the difference between one flush and
            // ten thousand.
            if (body.IsArray)
            {
                var documents = new List<CuteDocument>(body.Count);
                foreach (var item in body.AsArray.AsSpan())
                {
                    documents.Add(AsDocument(item));
                }

                var inserted = target.InsertMany(documents);
                await WriteValue(context, CuteValue.Object(new CuteObject()
                    .Set("inserted", inserted)
                    .Set("ids", CuteValue.ArrayOf(documents.Select(d => CuteValue.String(d.Id.ToString())).ToArray()))), 201);
                return;
            }

            var document = AsDocument(body);
            var id = target.Insert(document);
            context.Response.Headers.Location = $"/v1/collections/{collection}/documents/{id}";
            await WriteValue(context, document.AsValue(), 201);
        });

        app.MapPut("/v1/collections/{collection}/documents/{id}", async (
            HttpContext context,
            CuteDatabase database,
            string collection,
            string id) =>
        {
            if (!CuteId.TryParse(id, out var parsed))
            {
                await WriteValue(context, Error("invalid_id", $"'{id}' is not a document id."), 400);
                return;
            }

            var body = AsDocument(await ReadBody(context));

            // The URL is authoritative: a body carrying a different _id is a mistake worth
            // correcting rather than honouring.
            body.Root.Set(CuteDocument.IdField, CuteValue.Id(parsed));
            Require(database, collection).Save(body);
            await WriteValue(context, body.AsValue());
        });

        app.MapPatch("/v1/collections/{collection}/documents/{id}", async (
            HttpContext context,
            CuteDatabase database,
            string collection,
            string id) =>
        {
            if (!CuteId.TryParse(id, out var parsed))
            {
                await WriteValue(context, Error("invalid_id", $"'{id}' is not a document id."), 400);
                return;
            }

            var target = Require(database, collection);
            var existing = target.FindById(parsed);
            if (existing is null)
            {
                await WriteValue(context, Error("not_found", $"No document {id} in '{collection}'."), 404);
                return;
            }

            var patch = await ReadBody(context);
            if (!patch.IsObject)
            {
                await WriteValue(context, Error("invalid_body", "A patch must be a JSON object."), 400);
                return;
            }

            // A shallow merge, with the dotted keys CuteDB paths already use: {"address.city":"X"}
            // reaches into the subdocument, while {"address":{…}} replaces it wholesale. Both are
            // useful and neither can be expressed by the other.
            foreach (var (key, value) in patch.AsObject)
            {
                if (key.Contains('.') || key.Contains('['))
                {
                    CutePath.Parse(key).Assign(existing.Root, value);
                }
                else
                {
                    existing.Root.Set(key, value);
                }
            }

            target.Save(existing);
            await WriteValue(context, existing.AsValue());
        });

        app.MapDelete("/v1/collections/{collection}/documents/{id}", (
            CuteDatabase database,
            string collection,
            string id) =>
        {
            if (!CuteId.TryParse(id, out var parsed))
            {
                return Results.BadRequest(new { error = "invalid_id", message = $"'{id}' is not a document id." });
            }

            return Require(database, collection).Delete(parsed)
                ? Results.Json(new { deleted = id })
                : Results.NotFound(new { error = "not_found", message = $"No document {id} in '{collection}'." });
        });

        // --- queries -----------------------------------------------------------------------

        app.MapPost("/v1/query", async (HttpContext context, CuteDatabase database) =>
        {
            var body = await ReadBody(context);
            if (!body.IsObject)
            {
                await WriteValue(context, Error("invalid_body", "Send {\"query\": \"...\", \"parameters\": {...}}."), 400);
                return;
            }

            var query = body["query"];
            if (!query.TryGetString(out var text))
            {
                await WriteValue(context, Error("invalid_body", "The 'query' field is required and must be a string."), 400);
                return;
            }

            var parameters = ToParameters(body["parameters"]);
            var result = database.Execute(text, parameters);
            await WriteResult(context, result);
        });

        app.MapPost("/v1/explain", async (HttpContext context, CuteDatabase database) =>
        {
            var body = await ReadBody(context);
            if (!body["query"].TryGetString(out var text))
            {
                await WriteValue(context, Error("invalid_body", "The 'query' field is required."), 400);
                return;
            }

            var plan = database.Explain(text, ToParameters(body["parameters"]));
            await WriteValue(context, CuteValue.Object(new CuteObject()
                .Set("strategy", plan.Strategy)
                .Set("index", plan.IndexName is null ? CuteValue.Null : CuteValue.String(plan.IndexName))
                .Set("candidateRows", plan.CandidateRows)
                .Set("matchedRows", plan.MatchedRows)
                .Set("nativeScanner", plan.UsedNativeScanner)
                .Set("description", plan.ToString())));
        });

        // --- indexes -----------------------------------------------------------------------

        app.MapPost("/v1/collections/{collection}/indexes", async (
            HttpContext context,
            CuteDatabase database,
            string collection) =>
        {
            var body = await ReadBody(context);
            if (!body["path"].TryGetString(out var path))
            {
                await WriteValue(context, Error("invalid_body", "The 'path' field is required."), 400);
                return;
            }

            var name = body["name"].TryGetString(out var given) ? given : null;
            var unique = body["unique"].IsTruthy;

            var info = database.Collection(collection).CreateIndex(path, name, unique);
            await WriteValue(context, CuteValue.Object(new CuteObject()
                .Set("name", info.Name)
                .Set("path", info.Path)
                .Set("unique", info.Unique)
                .Set("keys", info.KeyCount)
                .Set("entries", info.EntryCount)), 201);
        });

        app.MapDelete("/v1/collections/{collection}/indexes/{name}", (
            CuteDatabase database,
            string collection,
            string name)
            => Require(database, collection).DropIndex(name)
                ? Results.Json(new { dropped = name })
                : Results.NotFound(new { error = "not_found", message = $"'{collection}' has no index called '{name}'." }));

        // --- maintenance -------------------------------------------------------------------

        app.MapGet("/v1/stats", (HttpContext context, CuteDatabase database) =>
        {
            var stats = database.Stats();
            return WriteValue(context, CuteValue.Object(new CuteObject()
                .Set("path", stats.Path ?? string.Empty)
                .Set("collections", stats.CollectionCount)
                .Set("documents", stats.DocumentCount)
                .Set("fileBytes", stats.FileBytes)
                .Set("liveBytes", stats.LiveBytes)
                .Set("deadBytes", stats.DeadBytes)
                .Set("reservedBytes", stats.ReservedBytes)
                .Set("fileAmplification", Math.Round(stats.FileAmplification, 2))
                .Set("createdAt", CuteValue.DateTime(stats.CreatedUtc))
                .Set("engine", CuteDatabase.EngineDescription)));
        });

        app.MapPost("/v1/compact", (CuteDatabase database)
            => Results.Json(new { reclaimedBytes = database.Compact() }));
    }

    private static CuteCollection Require(CuteDatabase database, string name)
        => database.TryGetCollection(name)
            ?? throw new CuteDbException(
                $"There is no collection called '{name}'. " +
                $"Existing: {(database.CollectionNames.Count == 0 ? "none" : string.Join(", ", database.CollectionNames))}.");

    private static CuteDocument AsDocument(CuteValue value)
        => value.IsObject
            ? new CuteDocument(value.AsObject)
            : throw new CuteDbException($"A document must be a JSON object, got {value.Type.ToDisplayName()}.");

    private static CuteValue Error(string code, string message)
        => CuteValue.Object(new CuteObject().Set("error", code).Set("message", message));

    private static async Task<CuteValue> ReadBody(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();

        return text.Trim().Length == 0
            ? throw new CuteDbException("The request body is empty.")
            : CuteJson.Parse(text, CuteJsonOptions.Financial);
    }

    /// <summary>
    /// Reads query parameters from the request's <c>?p.name=value</c> pairs, for the GET endpoints.
    /// </summary>
    private static CuteParameters? ReadParameters(HttpContext context)
    {
        CuteParameters? parameters = null;

        foreach (var (key, values) in context.Request.Query)
        {
            if (!key.StartsWith("p.", StringComparison.Ordinal) || values.Count == 0)
            {
                continue;
            }

            parameters ??= new CuteParameters();
            parameters.Set(key[2..], CuteValue.String(values[0] ?? string.Empty));
        }

        return parameters;
    }

    private static CuteParameters? ToParameters(CuteValue value)
    {
        if (!value.IsObject)
        {
            return null;
        }

        var parameters = new CuteParameters();
        foreach (var (name, bound) in value.AsObject)
        {
            parameters.Set(name, bound);
        }

        return parameters;
    }

    private static async Task WriteResult(HttpContext context, CuteQueryResult result)
    {
        var rows = new CuteArray(result.Rows.Count);
        foreach (var row in result.Rows)
        {
            rows.Add(CuteValue.Object(row));
        }

        var columns = new CuteArray(result.Columns.Count);
        foreach (var column in result.Columns)
        {
            columns.Add(CuteValue.String(column));
        }

        await WriteValue(context, CuteValue.Object(new CuteObject()
            .Set("kind", result.Kind.ToString().ToLowerInvariant())
            .Set("columns", CuteValue.Array(columns))
            .Set("rows", CuteValue.Array(rows))
            .Set("affected", result.AffectedCount)
            .Set("durationMs", Math.Round(result.Duration.TotalMilliseconds, 3))
            .Set("plan", CuteValue.String(result.Plan.ToString()))));
    }

    private static async Task WriteValue(HttpContext context, CuteValue value, int status = 200)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = JsonContentType;

        // Written through CuteDB's own writer rather than the framework serialiser, so decimals
        // stay exact and dates keep their shape.
        await context.Response.WriteAsync(CuteJson.Write(value), Encoding.UTF8);
    }

    private static async Task WriteRaw(HttpContext context, string json)
    {
        context.Response.ContentType = JsonContentType;
        await context.Response.WriteAsync(json, Encoding.UTF8);
    }

    /// <summary>
    /// Quotes a collection name for interpolation into a generated statement.
    /// </summary>
    /// <remarks>
    /// The name comes from the URL path, so it has to be checked rather than trusted. CuteQL has
    /// no quoted-identifier syntax, which makes rejection the only safe answer — and a collection
    /// name outside this character set could not have been created through this API anyway.
    /// </remarks>
    private static string Quote(string collection)
    {
        foreach (var c in collection)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
            {
                throw new CuteDbException(
                    $"'{collection}' is not a usable collection name here: letters, digits, '_' and '-' only.");
            }
        }

        return collection;
    }
}
