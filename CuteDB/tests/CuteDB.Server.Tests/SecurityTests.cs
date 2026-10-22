namespace CuteDB.Server.Tests;

/// <summary>The API key and CORS middleware — pinned as they behave, since clients depend on both.</summary>
public sealed class SecurityTests
{
    private const string Key = "rahasia-123";

    private static HttpRequestMessage Get(string path, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        configure?.Invoke(request);
        return request;
    }

    [Fact]
    public async Task With_no_key_configured_everything_is_open()
    {
        await using var server = await TestServerHost.StartAsync();

        Assert.Equal(200, (int)(await server.Client.GetAsync("/v1/stats")).StatusCode);
        Assert.Equal(200, (int)(await server.Client.GetAsync("/openapi.json")).StatusCode);

        // A key sent to a server that wants none is simply ignored.
        var response = await server.Client.SendAsync(Get("/v1/stats", r => r.Headers.Add("X-API-Key", "anything")));
        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task A_missing_key_is_a_401_with_a_json_body()
    {
        await using var server = await TestServerHost.StartAsync(apiKey: Key);

        var response = await server.Client.GetAsync("/v1/stats");

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        await response.ExpectErrorAsync(401, "unauthorized");
    }

    [Theory]
    [InlineData("X-API-Key", "wrong")]
    [InlineData("X-API-Key", "rahasia-12")]
    [InlineData("X-API-Key", "rahasia-1234")]
    [InlineData("X-API-Key", "")]
    [InlineData("Authorization", "Bearer wrong")]
    [InlineData("Authorization", "Bearer ")]
    [InlineData("Authorization", "Basic cmFoYXNpYS0xMjM=")]
    [InlineData("Authorization", "rahasia-123")]
    public async Task A_wrong_key_is_a_401(string header, string value)
    {
        await using var server = await TestServerHost.StartAsync(apiKey: Key);

        var request = Get("/v1/collections");
        request.Headers.TryAddWithoutValidation(header, value);

        await (await server.Client.SendAsync(request)).ExpectErrorAsync(401, "unauthorized");
    }

    [Theory]
    [InlineData("X-API-Key", Key)]
    [InlineData("Authorization", "Bearer " + Key)]
    [InlineData("Authorization", "bearer " + Key)]
    [InlineData("Authorization", "Bearer   " + Key + "  ")]
    public async Task The_right_key_is_accepted_as_a_header_or_a_bearer_token(string header, string value)
    {
        await using var server = await TestServerHost.StartAsync(apiKey: Key);

        var request = Get("/v1/collections");
        request.Headers.TryAddWithoutValidation(header, value);

        Assert.Equal(200, (int)(await server.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task X_API_Key_takes_precedence_over_a_bearer_token()
    {
        await using var server = await TestServerHost.StartAsync(apiKey: Key);

        var request = Get("/v1/collections", r =>
        {
            r.Headers.Add("X-API-Key", "wrong");
            r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Key);
        });

        Assert.Equal(401, (int)(await server.Client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Health_never_needs_a_key_and_everything_else_does()
    {
        await using var server = await TestServerHost.StartAsync(apiKey: Key);

        var health = await (await server.Client.GetAsync("/health")).ExpectAsync(200);
        Assert.Equal("ok", health.GetProperty("status").GetString());

        Assert.Equal(401, (int)(await server.Client.GetAsync("/openapi.json")).StatusCode);
        Assert.Equal(401, (int)(await server.QueryAsync(new { query = "SELECT * FROM x" })).StatusCode);
        Assert.Equal(401, (int)(await server.PostJsonAsync("/v1/collections/c/documents", """{"a":1}""")).StatusCode);
        Assert.Empty(server.Database.CollectionNames);
    }

    [Fact]
    public async Task A_cors_preflight_succeeds_without_a_key()
    {
        // A preflight never carries X-API-Key — it is asking permission to send it. The key check
        // used to run first, so every preflight got a 401 and --cors could not be combined with
        // --api-key at all.
        await using var server = await TestServerHost.StartAsync(apiKey: Key, origins: ["https://app.example.com"]);

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/v1/query");
        preflight.Headers.Add("Origin", "https://app.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "x-api-key, content-type");

        var response = await server.Client.SendAsync(preflight);

        Assert.Equal(204, (int)response.StatusCode);
        Assert.Equal("https://app.example.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("X-API-Key", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
        Assert.Contains("X-CuteDB-Next-Cursor", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public async Task A_401_to_a_listed_origin_still_carries_cors_headers_so_the_page_can_read_it()
    {
        await using var server = await TestServerHost.StartAsync(apiKey: Key, origins: ["https://app.example.com"]);

        var response = await server.Client.SendAsync(Get("/v1/stats", r => r.Headers.Add("Origin", "https://app.example.com")));

        Assert.Equal(401, (int)response.StatusCode);
        Assert.Equal("https://app.example.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task An_unlisted_origin_gets_no_cors_headers()
    {
        await using var server = await TestServerHost.StartAsync(origins: ["https://app.example.com"]);

        var response = await server.Client.SendAsync(Get("/v1/stats", r => r.Headers.Add("Origin", "https://evil.example.com")));

        Assert.Equal(200, (int)response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
