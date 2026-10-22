using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using CuteDB.Query;

namespace CuteDB.Server;

/// <summary>
/// Writes a query result to the response, as one JSON object or as newline-delimited JSON.
/// </summary>
/// <remarks>
/// <para>
/// Both modes write row by row into the response pipe and flush every
/// <see cref="FlushThreshold"/> bytes, so the server never holds a second, serialised copy of the
/// result — no <c>CuteArray</c> wrapping every row, no JSON string of the whole response. Memory
/// for the response is one flush window, whatever the row count.
/// </para>
/// <para>
/// What this does not change: the engine has already materialised every row in
/// <see cref="CuteQueryResult.Rows"/> before this runs. That is the engine's cursor item, not the
/// server's. It also means everything that can fail — parsing, planning, evaluating — has failed
/// before the status line is sent, so a client never receives a 200 followed by half a body and an
/// error it cannot see.
/// </para>
/// </remarks>
internal static class ResultWriter
{
    internal const string NdjsonMediaType = "application/x-ndjson";

    private const string JsonContentType = "application/json; charset=utf-8";
    private const string NdjsonContentType = NdjsonMediaType + "; charset=utf-8";

    /// <summary>
    /// Large enough that the flush is not the cost of a small row, small enough that a slow client
    /// does not make the server buffer much of a large result.
    /// </summary>
    private const int FlushThreshold = 32 * 1024;

    /// <summary>
    /// Whether the caller asked for NDJSON. Only an explicit <c>Accept: application/x-ndjson</c>
    /// does it; the three clients send <c>Accept: application/json</c> and so keep the shape they
    /// were written against.
    /// </summary>
    internal static bool WantsNdjson(HttpRequest request)
    {
        foreach (var header in request.Headers.Accept)
        {
            if (header is null)
            {
                continue;
            }

            foreach (var range in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var semicolon = range.IndexOf(';');
                var mediaType = (semicolon < 0 ? range : range[..semicolon]).Trim();
                if (mediaType.Equals(NdjsonMediaType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The shape the clients parse: <c>kind</c>, <c>columns</c>, <c>rows</c>, <c>affected</c>,
    /// <c>durationMs</c>, <c>plan</c> — plus <c>page</c> when, and only when, the caller paged.
    /// </summary>
    internal static async Task WriteJson(HttpContext context, CuteQueryResult result, QueryPage? page)
    {
        var (start, count) = Slice(result, page);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonContentType;

        var body = new Body(context.Response.BodyWriter);

        // The envelope is written as literal property names around values rendered by CuteJson,
        // so every value is formatted exactly as CuteJson.Write formatted it when this response
        // was built as one CuteObject — the bytes did not change, only when they are produced.
        body.Ascii("{\"kind\":");
        body.Value(CuteValue.String(KindName(result)));
        body.Ascii(",\"columns\":");
        body.Value(Columns(result));
        body.Ascii(",\"rows\":[");

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                body.Ascii(",");
            }

            body.Value(CuteValue.Object(result.Rows[start + i]));
            if (!await body.FlushIfFullAsync())
            {
                return;
            }
        }

        body.Ascii("],\"affected\":");
        body.Value(result.AffectedCount);
        body.Ascii(",\"durationMs\":");
        body.Value(DurationMs(result));
        body.Ascii(",\"plan\":");
        body.Value(CuteValue.String(result.Plan.ToString()));

        if (page is not null)
        {
            var next = page.NextCursor(result.Rows.Count);
            body.Ascii(",\"page\":");
            body.Value(CuteValue.Object(new CuteObject()
                .Set("offset", page.Offset)
                .Set("limit", page.Limit)
                .Set("returned", count)
                .Set("total", result.Rows.Count)
                .Set("next", next is null ? CuteValue.Null : CuteValue.String(next))));
        }

        body.Ascii("}");
        await body.FlushAsync();
    }

    /// <summary>
    /// One row per line and nothing else, so <c>for line in response</c> is the whole client.
    /// What the JSON envelope carries beside the rows travels in <c>X-CuteDB-*</c> headers.
    /// </summary>
    internal static async Task WriteNdjson(HttpContext context, CuteQueryResult result, QueryPage? page)
    {
        var (start, count) = Slice(result, page);

        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = NdjsonContentType;

        // Headers rather than a leading or trailing metadata line: a line that is sometimes a row
        // and sometimes not is a branch every consumer has to write and some will forget.
        var headers = response.Headers;
        headers["X-CuteDB-Kind"] = KindName(result);
        headers["X-CuteDB-Affected"] = result.AffectedCount.ToString(CultureInfo.InvariantCulture);
        headers["X-CuteDB-Duration-Ms"] = DurationMs(result).ToString(CultureInfo.InvariantCulture);
        headers["X-CuteDB-Plan"] = HeaderSafe(result.Plan.ToString());

        if (page is not null)
        {
            headers["X-CuteDB-Total"] = result.Rows.Count.ToString(CultureInfo.InvariantCulture);
            if (page.NextCursor(result.Rows.Count) is { } next)
            {
                headers["X-CuteDB-Next-Cursor"] = next;
            }
        }

        var body = new Body(response.BodyWriter);
        for (var i = 0; i < count; i++)
        {
            body.Value(CuteValue.Object(result.Rows[start + i]));
            body.Ascii("\n");
            if (!await body.FlushIfFullAsync())
            {
                return;
            }
        }

        await body.FlushAsync();
    }

    /// <summary>The headers a browser page may read from an NDJSON response, for CORS.</summary>
    internal const string ExposedHeaders =
        "X-CuteDB-Kind, X-CuteDB-Affected, X-CuteDB-Duration-Ms, X-CuteDB-Plan, X-CuteDB-Total, X-CuteDB-Next-Cursor";

    private static (int Start, int Count) Slice(CuteQueryResult result, QueryPage? page)
    {
        var total = result.Rows.Count;
        if (page is null)
        {
            return (0, total);
        }

        var start = Math.Min(page.Offset, total);
        return (start, Math.Min(page.Limit, total - start));
    }

    private static string KindName(CuteQueryResult result) => result.Kind.ToString().ToLowerInvariant();

    private static double DurationMs(CuteQueryResult result) => Math.Round(result.Duration.TotalMilliseconds, 3);

    /// <summary>
    /// The columns of the whole result, not of the page, so that every page of one query has the
    /// same columns and a table built from page one does not change shape on page two.
    /// </summary>
    private static CuteValue Columns(CuteQueryResult result)
    {
        var columns = new CuteArray(result.Columns.Count);
        foreach (var column in result.Columns)
        {
            columns.Add(CuteValue.String(column));
        }

        return CuteValue.Array(columns);
    }

    /// <summary>
    /// Percent-encodes anything outside printable ASCII, and '%' itself, so the value is legal in
    /// a header and decodes with any standard percent-decoder. An index name is the only part of a
    /// plan that is not already ASCII, so in practice this almost never changes anything.
    /// </summary>
    private static string HeaderSafe(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (b is >= 0x20 and < 0x7F && b != (byte)'%')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The response pipe plus a count of bytes written since the last flush. Counted here rather
    /// than read from <see cref="PipeWriter.UnflushedBytes"/> because not every pipe supports that
    /// — the test server's does not have to.
    /// </summary>
    private sealed class Body(PipeWriter pipe) : IBufferWriter<byte>
    {
        private long _unflushed;

        public void Advance(int count)
        {
            pipe.Advance(count);
            _unflushed += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => pipe.GetMemory(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => pipe.GetSpan(sizeHint);

        public void Ascii(string text)
        {
            var span = pipe.GetSpan(text.Length);
            var written = Encoding.ASCII.GetBytes(text, span);
            Advance(written);
        }

        public void Value(CuteValue value) => CuteJson.Write(this, value, CuteJsonOptions.Default);

        /// <summary>Flushes once a window's worth is buffered. False when the client has gone.</summary>
        public ValueTask<bool> FlushIfFullAsync()
            => _unflushed >= FlushThreshold ? FlushAsync() : ValueTask.FromResult(true);

        public async ValueTask<bool> FlushAsync()
        {
            _unflushed = 0;
            var flushed = await pipe.FlushAsync();

            // A completed pipe means the reader — the connection — is gone. Writing the rest of a
            // large result to nobody is the waste this check exists to avoid.
            return !flushed.IsCompleted && !flushed.IsCanceled;
        }
    }
}
