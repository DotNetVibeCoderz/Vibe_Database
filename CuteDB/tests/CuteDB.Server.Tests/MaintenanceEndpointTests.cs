using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CuteDB.Server.Tests;

/// <summary>Indexes, statistics, compaction and the OpenAPI description.</summary>
public sealed class MaintenanceEndpointTests
{
    private static Task<TestServerHost> StartWithCustomers()
        => TestServerHost.StartAsync(seed: db =>
            db.Collection("customers").InsertMany(Enumerable.Range(0, 50)
                .Select(i => new CuteDocument(new CuteObject()
                    .Set("email", $"c{i}@example.com")
                    .Set("city", i % 2 == 0 ? "Bandung" : "Medan")))
                .ToList()));

    [Fact]
    public async Task Indexes_are_created_used_listed_and_dropped()
    {
        await using var server = await StartWithCustomers();

        var created = await (await server.PostJsonAsync("/v1/collections/customers/indexes",
            """{"path":"city","name":"by_city"}""")).ExpectAsync(201);
        Assert.Equal("by_city", created.GetProperty("name").GetString());
        Assert.Equal("city", created.GetProperty("path").GetString());
        Assert.False(created.GetProperty("unique").GetBoolean());
        Assert.Equal(2, created.GetProperty("keys").GetInt32());
        Assert.Equal(50, created.GetProperty("entries").GetInt32());

        var collection = await (await server.Client.GetAsync("/v1/collections/customers")).ExpectAsync(200);
        var index = Assert.Single(collection.GetProperty("indexes").EnumerateArray());
        Assert.Equal("by_city", index.GetProperty("name").GetString());

        var plan = await (await server.PostJsonAsync("/v1/explain",
            """{"query":"SELECT * FROM customers WHERE city = 'Medan'"}""")).ExpectAsync(200);
        Assert.Equal("by_city", plan.GetProperty("index").GetString());
        Assert.Equal(25, plan.GetProperty("matchedRows").GetInt32());

        var dropped = await (await server.Client.DeleteAsync("/v1/collections/customers/indexes/by_city")).ExpectAsync(200);
        Assert.Equal("by_city", dropped.GetProperty("dropped").GetString());
        await (await server.Client.DeleteAsync("/v1/collections/customers/indexes/by_city")).ExpectErrorAsync(404, "not_found");
    }

    [Fact]
    public async Task A_unique_index_gets_a_default_name_and_refuses_duplicates()
    {
        await using var server = await StartWithCustomers();

        var created = await (await server.PostJsonAsync("/v1/collections/customers/indexes",
            """{"path":"email","unique":true}""")).ExpectAsync(201);
        Assert.True(created.GetProperty("unique").GetBoolean());
        Assert.False(string.IsNullOrEmpty(created.GetProperty("name").GetString()));

        await (await server.PostJsonAsync("/v1/collections/customers/documents", """{"email":"c3@example.com"}"""))
            .ExpectErrorAsync(400, "invalid_request");

        // The refused document must not linger: the engine once stored the row before finding
        // the conflict, so it stayed visible in memory while never reaching the log.
        var stats = await (await server.Client.GetAsync("/v1/stats")).ExpectAsync(200);
        Assert.Equal(50, stats.GetProperty("documents").GetInt32());

        // A unique index over values that are already duplicated cannot be built.
        await (await server.PostJsonAsync("/v1/collections/customers/indexes", """{"path":"city","unique":true}"""))
            .ExpectErrorAsync(400, "invalid_request");
    }

    [Fact]
    public async Task Index_requests_without_a_path_are_400s()
    {
        await using var server = await StartWithCustomers();

        await (await server.PostJsonAsync("/v1/collections/customers/indexes", """{"name":"x"}""")).ExpectErrorAsync(400, "invalid_body");
        await (await server.Client.DeleteAsync("/v1/collections/nope/indexes/x")).ExpectErrorAsync(400, "invalid_request");
    }

    [Fact]
    public async Task Stats_and_compact_report_on_the_whole_database()
    {
        await using var server = await StartWithCustomers();

        var stats = await (await server.Client.GetAsync("/v1/stats")).ExpectAsync(200);
        Assert.Equal(1, stats.GetProperty("collections").GetInt32());
        Assert.Equal(50, stats.GetProperty("documents").GetInt32());
        foreach (var field in new[] { "path", "fileBytes", "liveBytes", "deadBytes", "reservedBytes", "fileAmplification", "createdAt", "engine" })
        {
            Assert.True(stats.TryGetProperty(field, out _), field);
        }

        // Dates come back as ISO-8601 strings the Go client parses into time.Time.
        Assert.True(stats.GetProperty("createdAt").TryGetDateTimeOffset(out _));

        // Rewrite every document so the file holds history worth reclaiming.
        await (await server.QueryAsync(new { query = "UPDATE customers SET touched = true" })).ExpectAsync(200);
        var compacted = await (await server.PostJsonAsync("/v1/compact", "")).ExpectAsync(200);
        Assert.True(compacted.GetProperty("reclaimedBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task Health_names_the_engine()
    {
        await using var server = await TestServerHost.StartAsync();

        var health = await (await server.Client.GetAsync("/health")).ExpectAsync(200);
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.StartsWith("CuteDB", health.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task The_openapi_document_is_json_and_describes_every_mapped_route()
    {
        await using var server = await TestServerHost.StartAsync();

        var document = await (await server.Client.GetAsync("/openapi.json")).ExpectAsync(200);
        Assert.StartsWith("3.", document.GetProperty("openapi").GetString());
        var paths = document.GetProperty("paths");

        // Read the routes from the running app rather than listing them here, so a route added
        // without documentation fails this test instead of passing it.
        var routes = server.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => (
                Path: e.RoutePattern.RawText!,
                Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .ToList();

        Assert.True(routes.Count >= 15, $"only {routes.Count} routes found");

        foreach (var (path, methods) in routes)
        {
            Assert.True(paths.TryGetProperty(path, out var item), $"{path} is mapped but not in /openapi.json");
            foreach (var method in methods)
            {
                Assert.True(item.TryGetProperty(method.ToLowerInvariant(), out _), $"{method} {path} is mapped but not in /openapi.json");
            }
        }

        // And the other way round: nothing documented that is not served.
        foreach (var documented in paths.EnumerateObject())
        {
            Assert.Contains(routes, r => r.Path == documented.Name);
        }

        // The paging and streaming additions are described where a generator will find them.
        var query = paths.GetProperty("/v1/query").GetProperty("post");
        var properties = query.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("properties");
        foreach (var field in new[] { "query", "parameters", "limit", "offset", "cursor" })
        {
            Assert.True(properties.TryGetProperty(field, out _), field);
        }

        Assert.True(query.GetProperty("responses").GetProperty("200").GetProperty("content")
            .TryGetProperty("application/x-ndjson", out _));
        Assert.Equal(JsonValueKind.Object, document.GetProperty("components").GetProperty("schemas").GetProperty("Page").ValueKind);
    }
}
