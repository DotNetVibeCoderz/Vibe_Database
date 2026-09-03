using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CuteDB.Server;

/// <summary>
/// Rejects requests that do not carry the configured API key.
/// </summary>
/// <remarks>
/// The comparison is fixed-time. A naive string comparison leaks the key one character at a time
/// to anyone who can measure response latency, and getting that right costs one line.
/// </remarks>
public sealed class ApiKeyMiddleware(RequestDelegate next, ServerOptions options)
{
    private readonly byte[]? _expected = options.ApiKey is null ? null : Encoding.UTF8.GetBytes(options.ApiKey);

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (_expected is null || context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        if (!TryReadKey(context.Request, out var presented)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _expected))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(
                """{"error":"unauthorized","message":"Supply the API key as an X-API-Key header or a bearer token."}""");
            return;
        }

        await next(context);
    }

    private static bool TryReadKey(HttpRequest request, out string key)
    {
        if (request.Headers.TryGetValue("X-API-Key", out var header) && header.Count > 0)
        {
            key = header[0] ?? string.Empty;
            return key.Length > 0;
        }

        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            key = authorization["Bearer ".Length..].Trim();
            return key.Length > 0;
        }

        key = string.Empty;
        return false;
    }
}

/// <summary>
/// Answers CORS preflights and adds the response headers, for the origins the server was told
/// about.
/// </summary>
/// <remarks>
/// Deliberately not <c>AllowAnyOrigin</c>: a database API that any page on the internet can call
/// from a logged-in browser is a database API waiting to be abused. Origins must be listed.
/// </remarks>
public sealed class CorsMiddleware(RequestDelegate next, ServerOptions options)
{
    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (options.AllowedOrigins.Length > 0)
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                var headers = context.Response.Headers;
                headers.AccessControlAllowOrigin = origin;
                headers.AccessControlAllowHeaders = "Content-Type, X-API-Key, Authorization";
                headers.AccessControlAllowMethods = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
                headers.AccessControlMaxAge = "86400";

                // Origin varies the response, so a shared cache must not serve one origin's
                // response to another.
                headers.Vary = "Origin";
            }
        }

        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            return;
        }

        await next(context);
    }
}

/// <summary>
/// Turns an exception into a JSON error body with a sensible status code.
/// </summary>
/// <remarks>
/// CuteDB's own exceptions carry messages written for a person — including the caret line a query
/// error points with — so they are passed through as 400s. Anything else is a bug in the server
/// and becomes a 500 with a generic message, because an internal failure's text is not something
/// to hand to a caller.
/// </remarks>
public sealed class ProblemMiddleware(RequestDelegate next, ILogger<ProblemMiddleware> logger)
{
    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (CuteQueryException error)
        {
            await WriteProblem(context, HttpStatusCode.BadRequest, "invalid_query", error.Message);
        }
        catch (CuteCorruptionException error)
        {
            logger.LogError(error, "The database file is damaged.");
            await WriteProblem(context, HttpStatusCode.InternalServerError, "corrupt_database", error.Message);
        }
        catch (CuteDbException error)
        {
            await WriteProblem(context, HttpStatusCode.BadRequest, "invalid_request", error.Message);
        }
        catch (Exception error)
        {
            logger.LogError(error, "Unhandled failure serving {Method} {Path}.", context.Request.Method, context.Request.Path);
            await WriteProblem(
                context,
                HttpStatusCode.InternalServerError,
                "internal_error",
                "The server failed to handle this request. Check the server log.");
        }
    }

    private static async Task WriteProblem(HttpContext context, HttpStatusCode status, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new { error = code, message });
    }
}
