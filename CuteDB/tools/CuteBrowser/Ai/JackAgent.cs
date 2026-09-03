using System.Text.RegularExpressions;
using CuteDB.Browser.Ai.Plugins;
using CuteDB.Browser.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace CuteDB.Browser.Ai;

/// <summary>Who said a thing in the chat panel.</summary>
public enum ChatRole
{
    /// <summary>The person.</summary>
    User,

    /// <summary>Jack.</summary>
    Assistant,

    /// <summary>The app, reporting something rather than answering.</summary>
    System,
}

/// <summary>An image the person attached to a message.</summary>
/// <param name="FileName">What it was called.</param>
/// <param name="MimeType">Its media type.</param>
/// <param name="Data">The bytes.</param>
public sealed record ChatAttachment(string FileName, string MimeType, byte[] Data);

/// <summary>One message in the chat panel.</summary>
/// <param name="Role">Who said it.</param>
/// <param name="Text">What was said.</param>
/// <param name="At">When.</param>
/// <param name="Attachments">Any images that came with it.</param>
public sealed record ChatMessage(ChatRole Role, string Text, DateTime At, IReadOnlyList<ChatAttachment> Attachments)
{
    /// <summary>A message with nothing attached.</summary>
    public static ChatMessage Of(ChatRole role, string text) => new(role, text, DateTime.Now, []);

    /// <summary>
    /// The fenced code blocks in this message, in order.
    /// </summary>
    /// <remarks>
    /// This is what makes the panel useful rather than decorative: a query Jack writes can be sent
    /// to a tab with one click, and that only works if the block is found reliably. The language
    /// tag is kept so the tab opens in the right mode.
    /// </remarks>
    public IReadOnlyList<(string Language, string Code)> CodeBlocks => JackAgent.ExtractCode(Text);
}

/// <summary>
/// Jack — The Code Bender. The assistant in the right-hand panel.
/// </summary>
/// <remarks>
/// <para>
/// Semantic Kernel supplies the kernel, the plugins and the function-calling loop. Three of the
/// four providers speak OpenAI's API — OpenAI itself, Gemini through its compatibility endpoint,
/// and Ollama through its own — so they share one connector and differ only in base address, model
/// and key. Anthropic gets <see cref="AnthropicChatCompletionService"/>, which speaks the Messages
/// API directly.
/// </para>
/// <para>
/// A kernel is built per turn rather than kept, because the provider, the model and the key can all
/// change from the picker at the top of the panel between one message and the next, and a cached
/// kernel would keep answering as whoever it was built for.
/// </para>
/// <para>
/// History is trimmed to the last few turns. The system prompt is never trimmed: it is what makes
/// Jack check the schema before writing a query, and an assistant that forgets its brief halfway
/// through a conversation is worse than one that has no brief at all.
/// </para>
/// </remarks>
public sealed partial class JackAgent : IDisposable
{
    /// <summary>The assistant's name, as it appears in the panel and introduces itself.</summary>
    public const string Name = "Jack";

    /// <summary>The full name, for the panel header and the about box.</summary>
    public const string FullName = "Jack — The Code Bender";

    private readonly Workspace _workspace;
    private readonly ActivityLog _log;
    private readonly BrowserSettings _settings;
    private readonly WebPlugin _web;
    private readonly List<ChatMessage> _history = [];
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>Creates the assistant for one workspace.</summary>
    public JackAgent(Workspace workspace, BrowserSettings settings, ActivityLog log)
    {
        _workspace = workspace;
        _settings = settings;
        _log = log;
        _web = new WebPlugin(settings, log);
    }

    /// <summary>Everything said so far, oldest first.</summary>
    public IReadOnlyList<ChatMessage> History => _history;

    /// <summary>Raised whenever a message is added or the thread is cleared.</summary>
    public event Action? Changed;

    /// <summary>Whether a turn is in flight.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Which provider the next turn will use. Set from the picker.</summary>
    public AiProvider Provider
    {
        get => _settings.Provider;
        set
        {
            if (_settings.Provider != value)
            {
                _settings.Provider = value;
                _settings.Save();
                _log.Info("jack", $"Switched to {_settings.ProfileFor(value)}");
            }
        }
    }

    /// <summary>The greeting shown in an empty thread.</summary>
    public static string Greeting =>
        $"""
        Hello — I am **{FullName}**, the query assistant here.

        I can read the open database and write CuteQL or LINQ against what is actually in it. Ask me
        things like:

        - *"Which cities brought in the most revenue last quarter?"*
        - *"Why is this query slow?"* — paste it, and I will explain the plan
        - *"Rewrite this as LINQ"*
        - *"What does the orders collection look like?"*

        I check the schema before I write anything, and I validate every query before I hand it over.
        I do not run writes; if a statement changes data, I will give it to you to run.

        Press **Ctrl+Enter** to send.
        """;

    /// <summary>Sends a message and waits for the reply.</summary>
    public async Task SendAsync(string text, IReadOnlyList<ChatAttachment> attachments, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            return;
        }

        _history.Add(new ChatMessage(ChatRole.User, text, DateTime.Now, attachments));
        Changed?.Invoke();

        IsBusy = true;
        Changed?.Invoke();

        try
        {
            var profile = _settings.ProfileFor(_settings.Provider);

            if (profile.Provider != AiProvider.Ollama && string.IsNullOrWhiteSpace(profile.ApiKey))
            {
                Add(ChatRole.System,
                    $"There is no API key for {profile.Label}. Add one in **Tools ▸ Settings**, or set "
                    + $"the environment variable and restart. Ollama needs no key and runs locally.");

                return;
            }

            _log.Info("jack", $"Asking {profile} — {Collapse(text)}");

            var kernel = BuildKernel(profile);
            var chat = kernel.GetRequiredService<IChatCompletionService>();
            var history = BuildHistory();

            var reply = await AskAsync(chat, history, kernel, profile, token);

            var answer = reply.Content ?? string.Empty;
            Add(ChatRole.Assistant, string.IsNullOrWhiteSpace(answer)
                ? "I did not get an answer back. Try again, or check the endpoint in settings."
                : answer);

            _log.Good("jack", $"{profile.Label} replied ({answer.Length:N0} characters)");
        }
        catch (OperationCanceledException)
        {
            Add(ChatRole.System, "Stopped.");
        }
        catch (Exception exception)
        {
            Add(ChatRole.System, $"That did not work: {exception.Message}");
            _log.Bad("jack", exception.Message);
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Empties the thread.</summary>
    public void Clear()
    {
        _history.Clear();
        _log.Info("jack", "Chat cleared");
        Changed?.Invoke();
    }

    /// <summary>Adds a message from the app rather than from a turn.</summary>
    public void Add(ChatRole role, string text)
    {
        _history.Add(ChatMessage.Of(role, text));
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _web.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// Pulls the fenced code blocks out of a reply.
    /// </summary>
    /// <remarks>
    /// The language tag is normalised to what a tab understands: <c>cuteql</c> and <c>sql</c> both
    /// mean a CuteQL tab, <c>csharp</c> and <c>cs</c> both mean a LINQ tab. An untagged block is
    /// guessed from its first word, because models do sometimes forget the tag and a block that
    /// cannot be sent to a tab is a block the person has to copy by hand.
    /// </remarks>
    public static IReadOnlyList<(string Language, string Code)> ExtractCode(string text)
    {
        var blocks = new List<(string, string)>();

        foreach (Match match in Fence().Matches(text))
        {
            var tag = match.Groups["lang"].Value.Trim().ToLowerInvariant();
            var code = match.Groups["code"].Value.Trim('\r', '\n');

            if (code.Length == 0)
            {
                continue;
            }

            var language = tag switch
            {
                "cuteql" or "sql" or "cql" => "cuteql",
                "csharp" or "cs" or "c#" or "linq" => "csharp",
                "" => Guess(code),
                _ => tag,
            };

            blocks.Add((language, code));
        }

        return blocks;

        static string Guess(string code)
        {
            var first = code.TrimStart().Split([' ', '\n', '\r', '('], 2)[0].ToUpperInvariant();
            return first is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "EXPLAIN" ? "cuteql" : "csharp";
        }
    }

    /// <summary>
    /// One round trip, retried without a temperature if the model refuses one.
    /// </summary>
    /// <remarks>
    /// The reasoning models — the gpt-5 family, o1, o3 — reject any temperature but their default
    /// and return a 400 saying so. Hard-coding a list of which models those are would be wrong
    /// within a month, so the request is made as configured and the refusal is read: if the
    /// complaint names temperature, it is sent again without one. A person switching to a
    /// reasoning model should not have to know this, and should certainly not see a raw 400.
    /// </remarks>
    private async Task<ChatMessageContent> AskAsync(
        IChatCompletionService chat,
        ChatHistory history,
        Kernel kernel,
        ProviderProfile profile,
        CancellationToken token)
    {
        try
        {
            return await chat.GetChatMessageContentAsync(history, ExecutionSettings(profile), kernel, token);
        }
        catch (Exception exception) when (RefusesTemperature(exception))
        {
            _log.Info("jack", $"{profile.Model} does not accept a temperature; asking again without one.");
            return await chat.GetChatMessageContentAsync(
                history, ExecutionSettings(profile, withTemperature: false), kernel, token);
        }
    }

    private static bool RefusesTemperature(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("temperature", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private Kernel BuildKernel(ProviderProfile profile)
    {
        var builder = Kernel.CreateBuilder();

        if (profile.Provider == AiProvider.Anthropic)
        {
            builder.Services.AddSingleton<IChatCompletionService>(
                new AnthropicChatCompletionService(profile.Model, profile.ApiKey, profile.Endpoint));
        }
        else if (profile.Provider == AiProvider.AzureOpenAI)
        {
            // Azure addresses a deployment rather than a model, so the model field here is the
            // deployment name — which is usually, but not always, the same word.
            builder.AddAzureOpenAIChatCompletion(profile.Model, profile.Endpoint, profile.ApiKey);
        }
        else
        {
            // One HttpClient with a base address is how the OpenAI connector is pointed somewhere
            // else, which is what makes Gemini and Ollama work through the same code path.
            var http = new HttpClient(new SocketsHttpHandler(), disposeHandler: true)
            {
                BaseAddress = new Uri(profile.Endpoint.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromMinutes(5),
            };

            builder.AddOpenAIChatCompletion(
                profile.Model,
                string.IsNullOrWhiteSpace(profile.ApiKey) ? "not-needed" : profile.ApiKey,
                httpClient: http);
        }

        builder.Plugins.AddFromObject(new DatabasePlugin(_workspace, _log), "cutedb");
        builder.Plugins.AddFromObject(new ToolboxPlugin(_log), "toolbox");

        if (_settings.WebToolsEnabled)
        {
            builder.Plugins.AddFromObject(_web, "web");
        }

        return builder.Build();
    }

    private PromptExecutionSettings ExecutionSettings(ProviderProfile profile, bool withTemperature = true)
        => profile.Provider == AiProvider.Anthropic
            ? new AnthropicPromptExecutionSettings
            {
                Temperature = _settings.Temperature,
                MaxToolCalls = _settings.MaxToolCalls,
            }
            : new OpenAIPromptExecutionSettings
            {
                // Left null, the field is omitted from the request rather than sent as a default,
                // which is what a model that rejects the setting needs.
                Temperature = withTemperature ? _settings.Temperature : null,
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                    options: new FunctionChoiceBehaviorOptions { AllowConcurrentInvocation = false }),
            };

    private ChatHistory BuildHistory()
    {
        var history = new ChatHistory(SystemPrompt());

        // Older turns are dropped, not summarised: a summary of a conversation about queries loses
        // exactly the field names that made it useful, and a wrong field name is worse than a
        // forgotten one.
        var turns = Math.Max(_settings.HistoryTurns, 2);

        foreach (var message in _history.TakeLast(turns).Where(m => m.Role != ChatRole.System))
        {
            if (message.Role == ChatRole.Assistant)
            {
                history.AddAssistantMessage(message.Text);
                continue;
            }

            if (message.Attachments.Count == 0)
            {
                history.AddUserMessage(message.Text);
                continue;
            }

            var items = new ChatMessageContentItemCollection();
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                items.Add(new TextContent(message.Text));
            }

            foreach (var attachment in message.Attachments)
            {
                items.Add(new ImageContent(attachment.Data, attachment.MimeType));
            }

            history.AddUserMessage(items);
        }

        return history;
    }

    /// <summary>
    /// Jack's brief, plus what is true right now.
    /// </summary>
    /// <remarks>
    /// The configured prompt is the stable half; the open database is appended because it changes
    /// between turns. Telling the model which collections exist up front saves a tool call on most
    /// questions and, more importantly, stops it opening with a confident answer about a collection
    /// that is not there.
    /// </remarks>
    private string SystemPrompt()
    {
        var prompt = _settings.SystemPrompt;

        if (!_workspace.IsOpen)
        {
            return prompt + "\n\nNo database is open right now. If a question needs one, say so.";
        }

        var collections = _workspace.Collections();

        return prompt
            + $"\n\nOpen database: {_workspace.DisplayName}"
            + (collections.Count == 0
                ? " — no collections yet."
                : $"\nCollections: {string.Join(", ", collections)}."
                    + "\nUse describe_collection before writing a query against one you have not looked at yet.");
    }

    private static string Collapse(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= 90 ? single : single[..87] + "…";
    }

    [GeneratedRegex(@"```(?<lang>[A-Za-z0-9#+._-]*)[ \t]*\r?\n(?<code>[\s\S]*?)```", RegexOptions.Multiline)]
    private static partial Regex Fence();
}
