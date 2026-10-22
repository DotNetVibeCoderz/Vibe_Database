using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CuteDB.Server.Tests;

/// <summary>
/// The real server — same <see cref="ServerHost"/>, same middleware, same routes — over a database
/// on a throwaway path, with the in-memory test server in place of Kestrel.
/// </summary>
public sealed class TestServerHost : IAsyncDisposable
{
    private readonly string _directory;

    private TestServerHost(string directory, WebApplication app)
    {
        _directory = directory;
        App = app;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    /// <summary>The database the server is serving, for seeding and for checking what a request did.</summary>
    public CuteDatabase Database => App.Services.GetRequiredService<CuteDatabase>();

    public static async Task<TestServerHost> StartAsync(
        string? apiKey = null,
        string[]? origins = null,
        bool readOnly = false,
        Action<CuteDatabase>? seed = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cutedb-server-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.cute");

        if (seed is not null)
        {
            // Seeded before the server opens the file, so a read-only server can still be given data.
            using var database = CuteDatabase.Open(path);
            seed(database);
        }

        var options = new ServerOptions
        {
            DatabasePath = path,
            ApiKey = apiKey,
            AllowedOrigins = origins ?? [],
            ReadOnly = readOnly,
            Quiet = true,
        };

        var app = ServerHost.Build(options, configure: builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        return new TestServerHost(directory, app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();

        // StopAsync fires ApplicationStopping, which is where the server flushes and closes the
        // database — the same path an ordinary shutdown takes.
        await App.StopAsync();
        await App.DisposeAsync();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test over.
        }
    }

    public Task<HttpResponseMessage> PostJsonAsync(string path, string json, string? accept = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (accept is not null)
        {
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));
        }

        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string json)
        => Client.SendAsync(new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    /// <summary>Sends a query, as the three clients do: <c>{"query": …}</c> plus whatever else is given.</summary>
    public Task<HttpResponseMessage> QueryAsync(object body, string? accept = null)
        => PostJsonAsync("/v1/query", JsonSerializer.Serialize(body), accept);
}

/// <summary>Small assertions on HTTP responses that every test class wants.</summary>
public static class Http
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Asserts the status and returns the parsed body, with the body in the failure message.</summary>
    public static async Task<JsonElement> ExpectAsync(this HttpResponseMessage response, int status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True((int)response.StatusCode == status, $"Expected {status}, got {(int)response.StatusCode}: {text}");
        if (text.Length == 0)
        {
            return default;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Asserts a JSON error response: the status and the machine-readable code.</summary>
    public static async Task<string> ExpectErrorAsync(this HttpResponseMessage response, int status, string code)
    {
        var body = await response.ExpectAsync(status);
        Assert.Equal(code, body.GetProperty("error").GetString());
        var message = body.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message));
        return message!;
    }
}
