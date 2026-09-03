using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Services;

namespace CuteDB.Browser.Ai;

/// <summary>
/// Talks to Anthropic's Messages API, including the tool loop.
/// </summary>
/// <remarks>
/// <para>
/// Semantic Kernel's own Anthropic connector is alpha, and the OpenAI-compatibility shim in front
/// of the Messages API is documented as a convenience rather than the supported surface. Neither is
/// a good thing to build a shipped tool on, so this speaks the Messages API directly: it is a small
/// API, it is the one Anthropic actually supports, and it gives images and tools without a
/// translation layer in between.
/// </para>
/// <para>
/// The tool loop lives here rather than in the caller because that is where Semantic Kernel puts it
/// for every other provider — <c>FunctionChoiceBehavior.Auto()</c> means "call the functions for
/// me". The kernel's plugins are advertised as tools, a <c>tool_use</c> block is dispatched back
/// through <see cref="KernelFunction"/>, and the result is fed in as a <c>tool_result</c>. The loop
/// stops at <see cref="AnthropicPromptExecutionSettings.MaxToolCalls"/>, because a model that keeps
/// calling tools and never answers must eventually be told to answer.
/// </para>
/// </remarks>
public sealed class AnthropicChatCompletionService : IChatCompletionService
{
    private const string Version = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _model;

    /// <summary>Creates a service against one model.</summary>
    /// <param name="model">The model id, such as <c>claude-sonnet-5</c>.</param>
    /// <param name="apiKey">The API key.</param>
    /// <param name="endpoint">The base address, normally <c>https://api.anthropic.com/v1</c>.</param>
    /// <param name="http">An HTTP client to borrow, or null to make one.</param>
    public AnthropicChatCompletionService(string model, string apiKey, string endpoint, HttpClient? http = null)
    {
        _model = model;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        _http.BaseAddress = new Uri(endpoint.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", Version);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        Attributes = new Dictionary<string, object?> { [AIServiceExtensions.ModelIdKey] = model };
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> Attributes { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatHistory);

        var settings = AnthropicPromptExecutionSettings.From(executionSettings);
        var system = string.Join("\n\n", chatHistory
            .Where(m => m.Role == AuthorRole.System)
            .Select(m => m.Content)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

        var messages = new JsonArray();
        foreach (var message in chatHistory.Where(m => m.Role != AuthorRole.System))
        {
            messages.Add(ToMessage(message));
        }

        var tools = ToolsFrom(kernel);

        for (var round = 0; ; round++)
        {
            var reply = await SendAsync(system, messages, tools, settings, cancellationToken);
            var (text, calls) = ReadReply(reply);

            if (calls.Count == 0 || kernel is null || round >= settings.MaxToolCalls)
            {
                // Out of tool budget with calls still pending: answer with what there is, and say
                // so, rather than looping or returning an empty message.
                if (calls.Count > 0)
                {
                    text = string.IsNullOrWhiteSpace(text)
                        ? $"I stopped after {settings.MaxToolCalls} tool calls without reaching an answer. "
                            + "Ask me again, more narrowly, and I will try a shorter route."
                        : text;
                }

                return [new ChatMessageContent(AuthorRole.Assistant, text)];
            }

            // The assistant's turn has to go back verbatim, tool_use blocks included, or the
            // tool_result that follows has nothing to attach to.
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = reply["content"]!.DeepClone(),
            });

            var results = new JsonArray();
            foreach (var call in calls)
            {
                results.Add(await InvokeAsync(kernel, call, cancellationToken));
            }

            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Streaming is not implemented: the chat panel renders a turn once it is complete, because a
    /// half-written query is not useful to look at and a tool loop has nothing to stream during.
    /// The single message is yielded as one chunk so a streaming caller still works.
    /// </remarks>
    public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
        ChatHistory chatHistory,
        PromptExecutionSettings? executionSettings = null,
        Kernel? kernel = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = await GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);

        foreach (var message in messages)
        {
            yield return new StreamingChatMessageContent(message.Role, message.Content);
        }
    }

    private async Task<JsonObject> SendAsync(
        string system,
        JsonArray messages,
        JsonArray tools,
        AnthropicPromptExecutionSettings settings,
        CancellationToken token)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = settings.MaxTokens,
            ["temperature"] = settings.Temperature,
            ["messages"] = messages.DeepClone(),
        };

        if (!string.IsNullOrWhiteSpace(system))
        {
            body["system"] = system;
        }

        if (tools.Count > 0)
        {
            body["tools"] = tools.DeepClone();
        }

        using var response = await _http.PostAsJsonAsync("messages", body, token);
        var text = await response.Content.ReadAsStringAsync(token);

        if (!response.IsSuccessStatusCode)
        {
            // Anthropic puts a usable sentence in error.message; the raw body is a fallback for
            // the cases where it does not.
            var detail = TryReadError(text) ?? text;
            throw new HttpRequestException($"Anthropic returned {(int)response.StatusCode}: {detail}");
        }

        return JsonNode.Parse(text) as JsonObject
            ?? throw new HttpRequestException("Anthropic returned a body that was not an object.");
    }

    private static (string Text, List<ToolCall> Calls) ReadReply(JsonObject reply)
    {
        var text = new System.Text.StringBuilder();
        var calls = new List<ToolCall>();

        foreach (var block in reply["content"]?.AsArray() ?? [])
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "text":
                    text.Append(block["text"]?.GetValue<string>());
                    break;

                case "tool_use":
                    calls.Add(new ToolCall(
                        block["id"]?.GetValue<string>() ?? string.Empty,
                        block["name"]?.GetValue<string>() ?? string.Empty,
                        block["input"] as JsonObject ?? []));

                    break;
            }
        }

        return (text.ToString(), calls);
    }

    private static async Task<JsonObject> InvokeAsync(Kernel kernel, ToolCall call, CancellationToken token)
    {
        var result = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = call.Id,
        };

        try
        {
            var (plugin, function) = Split(call.Name);

            if (!kernel.Plugins.TryGetFunction(plugin, function, out var kernelFunction))
            {
                throw new KeyNotFoundException($"There is no tool called '{call.Name}'.");
            }

            var arguments = new KernelArguments();
            foreach (var (name, value) in call.Input)
            {
                arguments[name] = ToClr(value);
            }

            var outcome = await kernelFunction.InvokeAsync(kernel, arguments, token);
            result["content"] = outcome.ToString();
        }
        catch (Exception exception)
        {
            // A failing tool is information the model can use — a mistyped collection name, say —
            // so the error goes back as a result rather than aborting the turn.
            result["content"] = $"Error: {exception.Message}";
            result["is_error"] = true;
        }

        return result;
    }

    /// <summary>Turns the kernel's plugins into Anthropic tool declarations.</summary>
    private static JsonArray ToolsFrom(Kernel? kernel)
    {
        var tools = new JsonArray();
        if (kernel is null)
        {
            return tools;
        }

        foreach (var plugin in kernel.Plugins)
        {
            foreach (var function in plugin)
            {
                var properties = new JsonObject();
                var required = new JsonArray();

                foreach (var parameter in function.Metadata.Parameters)
                {
                    properties[parameter.Name] = new JsonObject
                    {
                        ["type"] = JsonTypeOf(parameter.ParameterType),
                        ["description"] = parameter.Description ?? string.Empty,
                    };

                    if (parameter.IsRequired)
                    {
                        required.Add(parameter.Name);
                    }
                }

                tools.Add(new JsonObject
                {
                    ["name"] = $"{plugin.Name}-{function.Name}",
                    ["description"] = function.Description ?? string.Empty,
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = required,
                    },
                });
            }
        }

        return tools;
    }

    private static JsonObject ToMessage(ChatMessageContent message)
    {
        var content = new JsonArray();

        foreach (var item in message.Items)
        {
            switch (item)
            {
                case TextContent { Text: { Length: > 0 } text }:
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    break;

                case ImageContent image when image.Data is { Length: > 0 } data:
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = image.MimeType ?? "image/png",
                            ["data"] = Convert.ToBase64String(data.Span),
                        },
                    });

                    break;
            }
        }

        if (content.Count == 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Content ?? string.Empty });
        }

        return new JsonObject
        {
            ["role"] = message.Role == AuthorRole.Assistant ? "assistant" : "user",
            ["content"] = content,
        };
    }

    private static (string Plugin, string Function) Split(string name)
    {
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? (string.Empty, name) : (name[..dash], name[(dash + 1)..]);
    }

    private static string JsonTypeOf(Type? type)
    {
        if (type is null)
        {
            return "string";
        }

        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(short))
        {
            return "integer";
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return "number";
        }

        return type.IsArray || (type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            ? "array"
            : "string";
    }

    private static object? ToClr(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
        JsonValue value when value.TryGetValue<long>(out var whole) => whole,
        JsonValue value when value.TryGetValue<double>(out var real) => real,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    private static string? TryReadError(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly record struct ToolCall(string Id, string Name, JsonObject Input);
}

/// <summary>What to ask Anthropic for.</summary>
public sealed class AnthropicPromptExecutionSettings : PromptExecutionSettings
{
    /// <summary>How much the model is allowed to wander.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>The cap on the reply's length.</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>How many rounds of tool calls before it has to answer.</summary>
    public int MaxToolCalls { get; set; } = 8;

    /// <summary>Reads these settings out of whatever the caller passed.</summary>
    public static AnthropicPromptExecutionSettings From(PromptExecutionSettings? settings) => settings switch
    {
        AnthropicPromptExecutionSettings anthropic => anthropic,
        null => new AnthropicPromptExecutionSettings(),
        _ => new AnthropicPromptExecutionSettings { ModelId = settings.ModelId },
    };
}
