using System.Net.Http.Headers;
using System.Text.Json;

namespace CuteDB.Server.Tests;

/// <summary>Collections and documents: the CRUD surface the three clients are built on.</summary>
public sealed class DocumentEndpointTests
{
    [Fact]
    public async Task Insert_get_replace_patch_delete_round_trip()
    {
        await using var server = await TestServerHost.StartAsync();

        var inserted = await server.PostJsonAsync("/v1/collections/orders/documents",
            """{"customer":"Sari","address":{"city":"Bandung","zip":"40111"},"total":249000}""");
        var created = await inserted.ExpectAsync(201);
        var id = created.GetProperty("_id").GetString()!;
        Assert.Matches("^[0-9a-f]{24}$", id);
        Assert.Equal($"/v1/collections/orders/documents/{id}", inserted.Headers.Location?.OriginalString);

        var fetched = await (await server.Client.GetAsync($"/v1/collections/orders/documents/{id}")).ExpectAsync(200);
        Assert.Equal("Sari", fetched.GetProperty("customer").GetString());
        Assert.Equal("Bandung", fetched.GetProperty("address").GetProperty("city").GetString());

        // PUT replaces wholesale, and the id in the URL wins over one in the body.
        var replaced = await (await server.SendJsonAsync(HttpMethod.Put, $"/v1/collections/orders/documents/{id}",
            """{"_id":"000000000000000000000000","customer":"Budi","total":5}""")).ExpectAsync(200);
        Assert.Equal(id, replaced.GetProperty("_id").GetString());
        Assert.Equal("Budi", replaced.GetProperty("customer").GetString());
        Assert.False(replaced.TryGetProperty("address", out _));

        // PATCH merges; a dotted key reaches into a subdocument without replacing it.
        await (await server.SendJsonAsync(HttpMethod.Put, $"/v1/collections/orders/documents/{id}",
            """{"customer":"Budi","address":{"city":"Bandung","zip":"40111"}}""")).ExpectAsync(200);
        var patched = await (await server.SendJsonAsync(HttpMethod.Patch, $"/v1/collections/orders/documents/{id}",
            """{"address.city":"Surabaya","status":"paid"}""")).ExpectAsync(200);
        Assert.Equal("Surabaya", patched.GetProperty("address").GetProperty("city").GetString());
        Assert.Equal("40111", patched.GetProperty("address").GetProperty("zip").GetString());
        Assert.Equal("paid", patched.GetProperty("status").GetString());

        var deleted = await (await server.Client.DeleteAsync($"/v1/collections/orders/documents/{id}")).ExpectAsync(200);
        Assert.Equal(id, deleted.GetProperty("deleted").GetString());

        await (await server.Client.GetAsync($"/v1/collections/orders/documents/{id}")).ExpectErrorAsync(404, "not_found");
        await (await server.Client.DeleteAsync($"/v1/collections/orders/documents/{id}")).ExpectErrorAsync(404, "not_found");
        await (await server.SendJsonAsync(HttpMethod.Patch, $"/v1/collections/orders/documents/{id}", """{"a":1}"""))
            .ExpectErrorAsync(404, "not_found");
    }

    [Fact]
    public async Task Inserting_an_array_inserts_every_document_and_returns_their_ids()
    {
        await using var server = await TestServerHost.StartAsync();

        var body = await (await server.PostJsonAsync("/v1/collections/items/documents",
            """[{"n":1},{"n":2},{"n":3}]""")).ExpectAsync(201);

        Assert.Equal(3, body.GetProperty("inserted").GetInt32());
        var ids = body.GetProperty("ids").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.Equal(3, server.Database.Collection("items").Count);
    }

    [Fact]
    public async Task Decimals_come_back_with_their_exact_digits()
    {
        await using var server = await TestServerHost.StartAsync();

        var created = await (await server.PostJsonAsync("/v1/collections/money/documents",
            """{"total":249000.10,"rate":0.1}""")).ExpectAsync(201);
        var id = created.GetProperty("_id").GetString();

        var response = await server.Client.GetAsync($"/v1/collections/money/documents/{id}");
        var text = await response.Content.ReadAsStringAsync();

        // Through a double, 0.1 would still print as 0.1 — but 249000.10 would lose its trailing
        // zero. The server parses with decimals preferred so the stored digits are the sent ones.
        Assert.Contains("249000.10", text);
    }

    [Fact]
    public async Task Bad_ids_and_bad_bodies_are_400s()
    {
        await using var server = await TestServerHost.StartAsync();
        await server.PostJsonAsync("/v1/collections/c/documents", """{"a":1}""");

        await (await server.Client.GetAsync("/v1/collections/c/documents/not-an-id")).ExpectErrorAsync(400, "invalid_id");
        await (await server.SendJsonAsync(HttpMethod.Put, "/v1/collections/c/documents/xyz", """{"a":1}""")).ExpectErrorAsync(400, "invalid_id");
        await (await server.SendJsonAsync(HttpMethod.Patch, "/v1/collections/c/documents/xyz", """{"a":1}""")).ExpectErrorAsync(400, "invalid_id");
        await (await server.Client.DeleteAsync("/v1/collections/c/documents/xyz")).ExpectErrorAsync(400, "invalid_id");

        // A document must be an object.
        await (await server.PostJsonAsync("/v1/collections/c/documents", "42")).ExpectErrorAsync(400, "invalid_request");
        await (await server.PostJsonAsync("/v1/collections/c/documents", "[1,2]")).ExpectErrorAsync(400, "invalid_request");
        await (await server.PostJsonAsync("/v1/collections/c/documents", "   ")).ExpectErrorAsync(400, "invalid_request");

        // A patch must be an object too.
        var id = (await (await server.PostJsonAsync("/v1/collections/c/documents", """{"a":1}""")).ExpectAsync(201))
            .GetProperty("_id").GetString();
        await (await server.SendJsonAsync(HttpMethod.Patch, $"/v1/collections/c/documents/{id}", "[1]")).ExpectErrorAsync(400, "invalid_body");
    }

    [Fact]
    public async Task Malformed_json_is_a_400_not_a_500()
    {
        await using var server = await TestServerHost.StartAsync();

        await (await server.PostJsonAsync("/v1/collections/c/documents", """{"a":""")).ExpectErrorAsync(400, "invalid_request");
        await (await server.PostJsonAsync("/v1/query", """{"query":""")).ExpectErrorAsync(400, "invalid_request");
    }

    [Fact]
    public async Task Collections_are_listed_described_and_dropped()
    {
        await using var server = await TestServerHost.StartAsync(seed: db =>
        {
            db.Collection("a").Insert(new CuteDocument(new CuteObject().Set("x", 1)));
            db.Collection("b").Insert(new CuteDocument(new CuteObject().Set("x", 1)));
            db.Collection("b").Insert(new CuteDocument(new CuteObject().Set("x", 2)));
        });

        var list = await (await server.Client.GetAsync("/v1/collections")).ExpectAsync(200);
        var byName = list.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e);
        Assert.Equal(1, byName["a"].GetProperty("documents").GetInt32());
        Assert.Equal(2, byName["b"].GetProperty("documents").GetInt32());
        foreach (var field in new[] { "indexes", "liveBytes", "averageDocumentBytes" })
        {
            Assert.True(byName["b"].TryGetProperty(field, out _), field);
        }

        var one = await (await server.Client.GetAsync("/v1/collections/b")).ExpectAsync(200);
        Assert.Equal("b", one.GetProperty("name").GetString());
        Assert.Equal(2, one.GetProperty("documents").GetInt32());
        Assert.Equal(JsonValueKind.Array, one.GetProperty("indexes").ValueKind);

        var dropped = await (await server.Client.DeleteAsync("/v1/collections/b")).ExpectAsync(200);
        Assert.Equal("b", dropped.GetProperty("dropped").GetString());
        await (await server.Client.DeleteAsync("/v1/collections/b")).ExpectErrorAsync(404, "not_found");

        // An unknown collection on a read is a 400 naming the ones that exist, not a 404 — the
        // message is the useful part, and this is what the clients have always received.
        var message = await (await server.Client.GetAsync("/v1/collections/nope")).ExpectErrorAsync(400, "invalid_request");
        Assert.Contains("a", message);
    }

    [Fact]
    public async Task Listing_documents_filters_and_pages_with_limit_and_offset()
    {
        await using var server = await TestServerHost.StartAsync(seed: db =>
            db.Collection("nums").InsertMany(Enumerable.Range(0, 30)
                .Select(i => new CuteDocument(new CuteObject()
                    .Set("n", i).Set("even", i % 2 == 0).Set("tag", i % 3 == 0 ? "fizz" : "plain")))
                .ToList()));

        var all = await (await server.Client.GetAsync("/v1/collections/nums/documents")).ExpectAsync(200);
        Assert.Equal(30, all.GetProperty("rows").GetArrayLength());

        var page = await (await server.Client.GetAsync("/v1/collections/nums/documents?filter=even%20%3D%20true&limit=5&offset=5"))
            .ExpectAsync(200);
        var rows = page.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("n").GetInt32()).ToList();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, n => Assert.Equal(0, n % 2));

        // ?p.name=value binds a parameter, so a GET filter need not concatenate values either.
        // Query-string values are always bound as strings.
        var bound = await (await server.Client.GetAsync("/v1/collections/nums/documents?filter=tag%20%3D%20%40t&p.t=fizz"))
            .ExpectAsync(200);
        Assert.Equal(10, bound.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Listing_documents_can_stream_ndjson()
    {
        await using var server = await TestServerHost.StartAsync(seed: db =>
            db.Collection("nums").InsertMany(Enumerable.Range(0, 12)
                .Select(i => new CuteDocument(new CuteObject().Set("n", i))).ToList()));

        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/collections/nums/documents?limit=10");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));
        var response = await server.Client.SendAsync(request);

        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(10, lines.Length);
    }

    [Fact]
    public async Task A_read_only_server_refuses_writes()
    {
        await using var server = await TestServerHost.StartAsync(readOnly: true, seed: db =>
            db.Collection("c").Insert(new CuteDocument(new CuteObject().Set("a", 1))));

        await (await server.Client.GetAsync("/v1/collections/c/documents")).ExpectAsync(200);

        var response = await server.PostJsonAsync("/v1/collections/c/documents", """{"a":2}""");
        Assert.Equal(400, (int)response.StatusCode);
        Assert.Equal(1, server.Database.Collection("c").Count);
    }
}
