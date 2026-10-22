using System.Text.Json;

namespace CuteDB.Server.Tests;

/// <summary>
/// <c>POST /v1/query</c>: the default shape the clients parse, paging, and NDJSON.
/// </summary>
public sealed class QueryEndpointTests
{
    private const int RowCount = 257;

    /// <summary>
    /// 257 orders — not a multiple of any page size used here, so the last page is always short —
    /// each carrying ~200 bytes of padding, so the whole result is well past the writer's 32 KiB
    /// flush window and the multi-flush path actually runs.
    /// </summary>
    private static Task<TestServerHost> StartWithOrders(string? apiKey = null)
        => TestServerHost.StartAsync(apiKey: apiKey, seed: db =>
            db.Collection("orders").InsertMany(Enumerable.Range(0, RowCount)
                .Select(i => new CuteDocument(new CuteObject()
                    .Set("n", i)
                    .Set("city", (i % 3) switch { 0 => "Bandung", 1 => "Jakarta", _ => "Surabaya" })
                    .Set("total", i * 1000)
                    .Set("note", new string('x', 200))))
                .ToList()));

    [Fact]
    public async Task The_default_response_keeps_the_shape_the_clients_parse()
    {
        await using var server = await StartWithOrders();

        var response = await server.QueryAsync(new { query = "SELECT * FROM orders" });
        var body = await response.ExpectAsync(200);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        // Exactly these properties, in this order, and no 'page' when none was asked for.
        Assert.Equal(
            ["kind", "columns", "rows", "affected", "durationMs", "plan"],
            body.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal("select", body.GetProperty("kind").GetString());
        Assert.Equal(RowCount, body.GetProperty("rows").GetArrayLength());
        Assert.Equal(RowCount, body.GetProperty("affected").GetInt32());
        Assert.Contains("city", body.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(JsonValueKind.Number, body.GetProperty("durationMs").ValueKind);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("plan").GetString()));
    }

    [Fact]
    public async Task Parameters_bind_and_aggregates_come_back_as_rows()
    {
        await using var server = await StartWithOrders();

        var body = await (await server.QueryAsync(new
        {
            query = "SELECT city, COUNT(*) AS n FROM orders WHERE city = @city GROUP BY city",
            parameters = new { city = "Bandung" },
        })).ExpectAsync(200);

        var row = Assert.Single(body.GetProperty("rows").EnumerateArray());
        Assert.Equal("Bandung", row.GetProperty("city").GetString());
        Assert.Equal(86, row.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Writes_report_their_kind_and_count()
    {
        await using var server = await StartWithOrders();

        var body = await (await server.QueryAsync(new { query = "UPDATE orders SET flagged = true WHERE n < 10" }))
            .ExpectAsync(200);

        Assert.Equal("update", body.GetProperty("kind").GetString());
        Assert.Equal(10, body.GetProperty("affected").GetInt32());
        Assert.Equal(0, body.GetProperty("rows").GetArrayLength());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(10_000)]
    public async Task Following_cursors_returns_the_whole_result_once_in_order(int limit)
    {
        await using var server = await StartWithOrders();
        const string query = "SELECT n, city FROM orders WHERE total >= @min ORDER BY n DESC";
        var parameters = new { min = 0 };

        var whole = (await (await server.QueryAsync(new { query, parameters })).ExpectAsync(200))
            .GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("n").GetInt32()).ToList();
        Assert.Equal(RowCount, whole.Count);

        var paged = new List<int>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var body = await (await server.QueryAsync(cursor is null
                    ? new { query, parameters, limit }
                    : (object)new { query, parameters, limit, cursor }))
                .ExpectAsync(200);

            var page = body.GetProperty("page");
            var rows = body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("n").GetInt32()).ToList();

            Assert.Equal(paged.Count, page.GetProperty("offset").GetInt32());
            Assert.Equal(limit, page.GetProperty("limit").GetInt32());
            Assert.Equal(rows.Count, page.GetProperty("returned").GetInt32());
            Assert.Equal(RowCount, page.GetProperty("total").GetInt32());

            // Every page has the columns of the whole result, so a table does not change shape.
            Assert.Equal(["n", "city"], body.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray());

            paged.AddRange(rows);
            cursor = page.GetProperty("next").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next").GetString();
            pages++;
            Assert.True(rows.Count == limit || cursor is null, "only the last page may be short");
            Assert.True(pages <= RowCount, "paging did not terminate");
        }
        while (cursor is not null);

        Assert.Equal(whole, paged);
        Assert.Equal(paged.Count, paged.Distinct().Count());
        Assert.Equal((RowCount + limit - 1) / limit, pages);
    }

    [Fact]
    public async Task Offset_pages_cover_the_result_without_gaps()
    {
        await using var server = await StartWithOrders();
        const string query = "SELECT n FROM orders";

        var seen = new List<int>();
        for (var offset = 0; offset < RowCount; offset += 40)
        {
            var body = await (await server.QueryAsync(new { query, limit = 40, offset })).ExpectAsync(200);
            seen.AddRange(body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("n").GetInt32()));
        }

        Assert.Equal(Enumerable.Range(0, RowCount).ToHashSet(), seen.ToHashSet());
        Assert.Equal(RowCount, seen.Count);
    }

    [Fact]
    public async Task Paging_works_within_a_statement_that_has_its_own_limit()
    {
        await using var server = await StartWithOrders();

        var body = await (await server.QueryAsync(new
        {
            query = "SELECT n FROM orders ORDER BY n LIMIT 25 OFFSET 10",
            limit = 10,
            offset = 20,
        })).ExpectAsync(200);

        Assert.Equal([30, 31, 32, 33, 34], body.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("n").GetInt32()).ToArray());
        Assert.Equal(25, body.GetProperty("page").GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("page").GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task An_offset_past_the_end_is_an_empty_last_page()
    {
        await using var server = await StartWithOrders();

        var body = await (await server.QueryAsync(new { query = "SELECT * FROM orders", limit = 10, offset = 5000 }))
            .ExpectAsync(200);

        Assert.Equal(0, body.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, body.GetProperty("page").GetProperty("returned").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("page").GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task Asking_for_a_page_without_a_limit_gets_the_default_of_100()
    {
        await using var server = await StartWithOrders();

        var body = await (await server.QueryAsync(new { query = "SELECT * FROM orders", offset = 0 })).ExpectAsync(200);

        Assert.Equal(100, body.GetProperty("rows").GetArrayLength());
        Assert.Equal(100, body.GetProperty("page").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Null_paging_fields_mean_no_paging()
    {
        await using var server = await StartWithOrders();

        var body = await (await server.PostJsonAsync("/v1/query",
            """{"query":"SELECT * FROM orders","limit":null,"offset":null,"cursor":null}""")).ExpectAsync(200);

        Assert.False(body.TryGetProperty("page", out _));
        Assert.Equal(RowCount, body.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Bad_paging_requests_are_refused()
    {
        await using var server = await StartWithOrders();
        const string query = "SELECT * FROM orders";

        await (await server.QueryAsync(new { query, limit = 0 })).ExpectErrorAsync(400, "invalid_body");
        await (await server.QueryAsync(new { query, limit = 10_001 })).ExpectErrorAsync(400, "invalid_body");
        await (await server.QueryAsync(new { query, limit = 1.5 })).ExpectErrorAsync(400, "invalid_body");
        await (await server.QueryAsync(new { query, limit = "10" })).ExpectErrorAsync(400, "invalid_body");
        await (await server.QueryAsync(new { query, offset = -1 })).ExpectErrorAsync(400, "invalid_body");

        var first = await (await server.QueryAsync(new { query, limit = 10 })).ExpectAsync(200);
        var cursor = first.GetProperty("page").GetProperty("next").GetString();

        await (await server.QueryAsync(new { query, offset = 10, cursor })).ExpectErrorAsync(400, "invalid_body");
        await (await server.QueryAsync(new { query, cursor = "not a cursor" })).ExpectErrorAsync(400, "invalid_cursor");
        await (await server.QueryAsync(new { query, cursor = 12 })).ExpectErrorAsync(400, "invalid_cursor");

        // A cursor is bound to the statement and its parameters that produced it.
        await (await server.QueryAsync(new { query = "SELECT n FROM orders", cursor })).ExpectErrorAsync(400, "invalid_cursor");
        await (await server.QueryAsync(new { query, parameters = new { x = 1 }, cursor })).ExpectErrorAsync(400, "invalid_cursor");

        // The same cursor with the same query works.
        await (await server.QueryAsync(new { query, limit = 10, cursor })).ExpectAsync(200);
    }

    [Fact]
    public async Task Paging_a_write_is_refused_before_the_write_happens()
    {
        await using var server = await StartWithOrders();

        await (await server.QueryAsync(new { query = "DELETE FROM orders", limit = 10 })).ExpectErrorAsync(400, "invalid_body");

        Assert.Equal(RowCount, server.Database.Collection("orders").Count);
    }

    [Fact]
    public async Task Ndjson_is_one_parseable_row_per_line()
    {
        await using var server = await StartWithOrders();

        var response = await server.QueryAsync(new { query = "SELECT * FROM orders ORDER BY n" }, accept: "application/x-ndjson");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        var text = await response.Content.ReadAsStringAsync();
        Assert.EndsWith("\n", text);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(RowCount, lines.Length);

        var numbers = lines.Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            return document.RootElement.GetProperty("n").GetInt32();
        }).ToList();
        Assert.Equal(Enumerable.Range(0, RowCount), numbers);

        Assert.Equal("select", Header(response, "X-CuteDB-Kind"));
        Assert.Equal(RowCount.ToString(), Header(response, "X-CuteDB-Affected"));
        Assert.NotNull(Header(response, "X-CuteDB-Duration-Ms"));
        Assert.NotNull(Header(response, "X-CuteDB-Plan"));
        Assert.Null(Header(response, "X-CuteDB-Total"));
        Assert.Null(Header(response, "X-CuteDB-Next-Cursor"));
    }

    [Fact]
    public async Task Ndjson_pages_carry_their_cursor_in_a_header()
    {
        await using var server = await StartWithOrders();
        const string query = "SELECT n FROM orders ORDER BY n";

        var seen = new List<int>();
        string? cursor = null;
        do
        {
            var response = await server.QueryAsync(
                cursor is null ? new { query, limit = 100 } : (object)new { query, limit = 100, cursor },
                accept: "application/x-ndjson");
            Assert.Equal(200, (int)response.StatusCode);
            Assert.Equal(RowCount.ToString(), Header(response, "X-CuteDB-Total"));

            foreach (var line in (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                seen.Add(document.RootElement.GetProperty("n").GetInt32());
            }

            cursor = Header(response, "X-CuteDB-Next-Cursor");
        }
        while (cursor is not null);

        Assert.Equal(Enumerable.Range(0, RowCount), seen);
    }

    [Fact]
    public async Task Ndjson_for_a_write_is_an_empty_body_with_the_count_in_a_header()
    {
        await using var server = await StartWithOrders();

        var response = await server.QueryAsync(new { query = "DELETE FROM orders WHERE n < 7" }, accept: "application/x-ndjson");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal("delete", Header(response, "X-CuteDB-Kind"));
        Assert.Equal("7", Header(response, "X-CuteDB-Affected"));
    }

    [Fact]
    public async Task Accept_application_json_as_the_clients_send_it_keeps_the_json_object()
    {
        await using var server = await StartWithOrders();

        var response = await server.QueryAsync(new { query = "SELECT * FROM orders LIMIT 3" }, accept: "application/json");

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(3, (await response.ExpectAsync(200)).GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task A_query_error_is_a_400_with_the_caret_line()
    {
        await using var server = await StartWithOrders();

        var message = await (await server.QueryAsync(new { query = "SELECT * FROM orders WHERE total ~ 5" }))
            .ExpectErrorAsync(400, "invalid_query");
        Assert.Contains("^", message);

        // The NDJSON mode fails the same way: everything that can fail does so before the first row.
        var streamed = await server.QueryAsync(new { query = "SELECT nope(" }, accept: "application/x-ndjson");
        Assert.Equal(400, (int)streamed.StatusCode);
        Assert.Equal("application/problem+json", streamed.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_missing_or_misshapen_query_is_a_400()
    {
        await using var server = await StartWithOrders();

        await (await server.PostJsonAsync("/v1/query", """{"parameters":{}}""")).ExpectErrorAsync(400, "invalid_body");
        await (await server.PostJsonAsync("/v1/query", """{"query":42}""")).ExpectErrorAsync(400, "invalid_body");
        await (await server.PostJsonAsync("/v1/query", """["SELECT * FROM orders"]""")).ExpectErrorAsync(400, "invalid_body");
        await (await server.PostJsonAsync("/v1/query", "")).ExpectErrorAsync(400, "invalid_request");
        await (await server.QueryAsync(new { query = "SELECT * FROM missing" })).ExpectErrorAsync(400, "invalid_query");
    }

    [Fact]
    public async Task Explain_reports_the_plan_without_rows()
    {
        await using var server = await StartWithOrders();

        var body = await (await server.QueryAsync(new { query = "SELECT * FROM orders WHERE city = 'Bandung'" }))
            .ExpectAsync(200);
        Assert.Equal(86, body.GetProperty("rows").GetArrayLength());

        var plan = await (await server.PostJsonAsync("/v1/explain",
            """{"query":"SELECT * FROM orders WHERE city = 'Bandung'"}""")).ExpectAsync(200);
        Assert.Equal(86, plan.GetProperty("matchedRows").GetInt32());
        Assert.Equal(RowCount, plan.GetProperty("candidateRows").GetInt32());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("index").ValueKind);
        foreach (var field in new[] { "strategy", "nativeScanner", "description" })
        {
            Assert.True(plan.TryGetProperty(field, out _), field);
        }

        await (await server.PostJsonAsync("/v1/explain", """{"query":"DELETE FROM orders"}""")).ExpectErrorAsync(400, "invalid_request");
        await (await server.PostJsonAsync("/v1/explain", """{}""")).ExpectErrorAsync(400, "invalid_body");
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
