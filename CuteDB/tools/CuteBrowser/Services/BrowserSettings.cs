using System.Configuration;
using System.Globalization;

namespace CuteDB.Browser.Services;

/// <summary>Which service Jack talks to.</summary>
public enum AiProvider
{
    /// <summary>OpenAI, or anything that speaks its API at a different address.</summary>
    OpenAI,

    /// <summary>Anthropic's Messages API.</summary>
    Anthropic,

    /// <summary>Google Gemini, through its OpenAI-compatible endpoint.</summary>
    Gemini,

    /// <summary>A local Ollama, through its OpenAI-compatible endpoint.</summary>
    Ollama,

    /// <summary>
    /// Azure OpenAI. Separate from <see cref="OpenAI"/> because it authenticates with an
    /// <c>api-key</c> header against a deployment URL rather than a model name, which no amount of
    /// endpoint configuration on the OpenAI path can express.
    /// </summary>
    AzureOpenAI,

    /// <summary>
    /// Anything else that speaks OpenAI's API at another address — DeepSeek, Groq, Together,
    /// OpenRouter, a vLLM server.
    /// </summary>
    /// <remarks>
    /// Worth its own entry rather than borrowing the OpenAI one: a picker that says "OpenAI" while
    /// the request goes to DeepSeek is a picker that lies about where the data went.
    /// </remarks>
    Compatible,
}

/// <summary>One provider's connection details.</summary>
/// <param name="Provider">Which service.</param>
/// <param name="Model">The model id to ask for.</param>
/// <param name="ApiKey">The key, or empty when the environment supplies it.</param>
/// <param name="Endpoint">The base address.</param>
public sealed record ProviderProfile(AiProvider Provider, string Model, string ApiKey, string Endpoint)
{
    /// <summary>The provider's name as it appears in the model picker.</summary>
    public string Label => Provider switch
    {
        AiProvider.OpenAI => "OpenAI",
        AiProvider.Anthropic => "Claude",
        AiProvider.Gemini => "Gemini",
        AiProvider.Ollama => "Ollama",
        AiProvider.Compatible => "Compatible",
        AiProvider.AzureOpenAI => "Azure OpenAI",
        _ => Provider.ToString(),
    };

    /// <summary>What the picker shows: who, and which model.</summary>
    public override string ToString() => $"{Label} · {Model}";
}

/// <summary>
/// Everything in <c>app.config</c>, read at startup and written back when it changes.
/// </summary>
/// <remarks>
/// <para>
/// The user asked for the configuration to live in <c>app.config</c> and to be editable from the
/// UI, which means the file has to be both readable by <see cref="ConfigurationManager"/> and
/// writable at runtime. <see cref="ConfigurationManager"/> caches, so a save refreshes the section
/// rather than trusting the cache — otherwise a setting changed in the dialog reads back stale.
/// </para>
/// <para>
/// API keys fall back to environment variables. A blank key in the file means "look at the
/// environment"; a filled one wins. That way a checked-out copy of this repository never needs to
/// carry somebody's key, and a machine that has <c>OPENAI_API_KEY</c> set already works.
/// </para>
/// </remarks>
public sealed class BrowserSettings
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    private BrowserSettings()
    {
    }

    /// <summary>The settings for this process.</summary>
    public static BrowserSettings Current { get; } = Load();

    /// <summary>Raised after any save, so open panels can pick the change up.</summary>
    public event Action? Changed;

    // ---- the assistant -------------------------------------------------------------------

    /// <summary>The provider Jack currently uses.</summary>
    public AiProvider Provider
    {
        get => Enum.TryParse<AiProvider>(Get("ai.provider", "openai"), ignoreCase: true, out var value)
            ? value
            : AiProvider.OpenAI;
        set => Set("ai.provider", value.ToString().ToLowerInvariant());
    }

    /// <summary>How much the model is allowed to wander. Query writing wants the low end.</summary>
    public double Temperature
    {
        get => Number("ai.temperature", 0.2);
        set => Set("ai.temperature", value.ToString("0.##", CultureInfo.InvariantCulture));
    }

    /// <summary>How many previous turns Jack is shown.</summary>
    public int HistoryTurns
    {
        get => (int)Number("ai.historyTurns", 24);
        set => Set("ai.historyTurns", value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>How many tool calls the model may make before it has to answer.</summary>
    public int MaxToolCalls
    {
        get => (int)Number("ai.maxToolCalls", 8);
        set => Set("ai.maxToolCalls", value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Jack's brief.</summary>
    public string SystemPrompt
    {
        get => Get("ai.systemPrompt", "You are Jack — The Code Bender, the query assistant in CuteDB Browser.");
        set => Set("ai.systemPrompt", value);
    }

    // ---- tools ---------------------------------------------------------------------------

    /// <summary>The Tavily key, for <c>search_internet</c>.</summary>
    public string TavilyKey
    {
        get => KeyOrEnvironment("tavily.apiKey", "TAVILY_API_KEY");
        set => Set("tavily.apiKey", value);
    }

    /// <summary>Where Tavily lives.</summary>
    public string TavilyEndpoint => Get("tavily.endpoint", "https://api.tavily.com");

    /// <summary>Whether the web tools are offered to the model at all.</summary>
    public bool WebToolsEnabled
    {
        get => Flag("tools.web", true);
        set => Set("tools.web", value ? "true" : "false");
    }

    /// <summary>How much of a scraped page is handed to the model.</summary>
    public int MaxScrapeChars => (int)Number("tools.maxScrapeChars", 12_000);

    // ---- the workbench -------------------------------------------------------------------

    /// <summary>Whether new editor tabs show a line-number margin.</summary>
    public bool ShowLineNumbers
    {
        get => Flag("editor.showLineNumbers", true);
        set => Set("editor.showLineNumbers", value ? "true" : "false");
    }

    /// <summary>The editor's point size.</summary>
    public double EditorFontSize
    {
        get => Number("editor.fontSize", 13.5);
        set => Set("editor.fontSize", value.ToString("0.##", CultureInfo.InvariantCulture));
    }

    /// <summary>Whether the editor wraps long lines.</summary>
    public bool WordWrap
    {
        get => Flag("editor.wordWrap", false);
        set => Set("editor.wordWrap", value ? "true" : "false");
    }

    /// <summary>How many rows the grid holds at once.</summary>
    public int ResultsPageSize
    {
        get => (int)Number("results.pageSize", 500);
        set => Set("results.pageSize", value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>How wide the explorer is, in pixels.</summary>
    public double ExplorerWidth
    {
        get => Number("explorer.width", 300);
        set => Set("explorer.width", value.ToString("0", CultureInfo.InvariantCulture));
    }

    /// <summary>Whether the chat panel is showing.</summary>
    public bool ChatVisible
    {
        get => Flag("chat.visible", true);
        set => Set("chat.visible", value ? "true" : "false");
    }

    /// <summary>How wide the chat panel is, in pixels.</summary>
    public double ChatWidth
    {
        get => Number("chat.width", 380);
        set => Set("chat.width", value.ToString("0", CultureInfo.InvariantCulture));
    }

    /// <summary>Whether the log panel is showing.</summary>
    public bool LogsVisible
    {
        get => Flag("logs.visible", true);
        set => Set("logs.visible", value ? "true" : "false");
    }

    /// <summary>How tall the log panel is, in pixels.</summary>
    public double LogsHeight
    {
        get => Number("logs.height", 150);
        set => Set("logs.height", value.ToString("0", CultureInfo.InvariantCulture));
    }

    /// <summary>The database reopened on the next launch.</summary>
    public string LastDatabase
    {
        get => Get("workspace.lastDatabase", string.Empty);
        set => Set("workspace.lastDatabase", value);
    }

    /// <summary>Recently opened files, newest first.</summary>
    public IReadOnlyList<string> Recent
    {
        get => Get("workspace.recent", string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        set => Set("workspace.recent", string.Join('|', value.Take(10)));
    }

    /// <summary>Adds a file to the recent list, newest first, without duplicates.</summary>
    public void Remember(string path)
    {
        var recent = new List<string> { path };
        recent.AddRange(Recent.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));
        Recent = recent;
        LastDatabase = path;
        Save();
    }

    // ---- providers -----------------------------------------------------------------------

    /// <summary>The connection details for one provider.</summary>
    public ProviderProfile ProfileFor(AiProvider provider)
    {
        var (prefix, environment, model, endpoint) = provider switch
        {
            AiProvider.OpenAI => ("openai", "OPENAI_API_KEY", "gpt-4o", "https://api.openai.com/v1"),
            AiProvider.Anthropic => ("anthropic", "ANTHROPIC_API_KEY", "claude-sonnet-5", "https://api.anthropic.com/v1"),
            AiProvider.Gemini => ("gemini", "GEMINI_API_KEY", "gemini-2.0-flash", "https://generativelanguage.googleapis.com/v1beta/openai/"),
            AiProvider.Compatible => ("compatible", "OPENAI_COMPATIBLE_API_KEY", "deepseek-chat", "https://api.deepseek.com/v1"),
            AiProvider.AzureOpenAI => ("azure", "AZURE_OPENAI_API_KEY", "gpt-4o", "https://your-resource.openai.azure.com/"),
            _ => ("ollama", "OLLAMA_API_KEY", "llama3.1", "http://localhost:11434/v1"),
        };

        return new ProviderProfile(
            provider,
            Get($"{prefix}.model", model),
            KeyOrEnvironment($"{prefix}.apiKey", environment),
            Get($"{prefix}.endpoint", endpoint));
    }

    /// <summary>Writes one provider's details back.</summary>
    public void UpdateProfile(ProviderProfile profile)
    {
        var prefix = profile.Provider.ToString().ToLowerInvariant();
        Set($"{prefix}.model", profile.Model);
        Set($"{prefix}.apiKey", profile.ApiKey);
        Set($"{prefix}.endpoint", profile.Endpoint);
    }

    /// <summary>Every provider, for the model picker at the top of the chat panel.</summary>
    public IReadOnlyList<ProviderProfile> AllProfiles()
        => [.. Enum.GetValues<AiProvider>().Select(ProfileFor)];

    // ---- reading and writing ---------------------------------------------------------------

    /// <summary>One raw setting.</summary>
    public string Get(string key, string fallback = "")
        => _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    /// <summary>Sets one raw setting in memory. Call <see cref="Save"/> to persist.</summary>
    public void Set(string key, string value) => _values[key] = value ?? string.Empty;

    /// <summary>
    /// Writes every setting back to <c>app.config</c> beside the executable.
    /// </summary>
    /// <remarks>
    /// A failure here is reported rather than thrown: a read-only install directory is a real
    /// situation, and losing the settings you just typed is worse than the app forgetting them
    /// after you close it. The return value says which happened.
    /// </remarks>
    public bool Save(out string message)
    {
        try
        {
            var configuration = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);

            foreach (var (key, value) in _values)
            {
                if (configuration.AppSettings.Settings[key] is null)
                {
                    configuration.AppSettings.Settings.Add(key, value);
                }
                else
                {
                    configuration.AppSettings.Settings[key].Value = value;
                }
            }

            configuration.Save(ConfigurationSaveMode.Modified);

            // The manager caches the section it just wrote, so a read straight after a save would
            // return what was there before.
            ConfigurationManager.RefreshSection("appSettings");

            message = configuration.FilePath;
            Changed?.Invoke();
            return true;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            Changed?.Invoke();
            return false;
        }
    }

    /// <summary>Writes every setting back, ignoring whether it worked.</summary>
    public void Save() => Save(out _);

    private static BrowserSettings Load()
    {
        var settings = new BrowserSettings();

        foreach (var key in ConfigurationManager.AppSettings.AllKeys)
        {
            if (key is not null)
            {
                settings._values[key] = ConfigurationManager.AppSettings[key] ?? string.Empty;
            }
        }

        return settings;
    }

    private string KeyOrEnvironment(string key, string variable)
    {
        var configured = Get(key);
        return string.IsNullOrWhiteSpace(configured)
            ? Environment.GetEnvironmentVariable(variable) ?? string.Empty
            : configured;
    }

    private double Number(string key, double fallback)
        => double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private bool Flag(string key, bool fallback)
        => bool.TryParse(Get(key), out var value) ? value : fallback;
}
