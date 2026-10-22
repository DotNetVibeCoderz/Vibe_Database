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
/// UI. There are two files in that format, read in order:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>The shipped defaults</b> — <c>CuteBrowser.dll.config</c>, which the build makes from
/// <c>App.config</c> and puts beside the executable. Read, never written.
/// </description></item>
/// <item><description>
/// <b>The person's own settings</b> — <see cref="UserFile"/>, in their application-data folder.
/// Everything Tools ▸ Settings changes is written there.
/// </description></item>
/// </list>
/// <para>
/// The split exists because of <c>dotnet tool</c>. A tool is unpacked into a versioned directory
/// in the tool store, which <c>dotnet tool update</c> replaces wholesale — settings written beside
/// the executable would be lost on every update, and on a machine-wide install they could not be
/// written at all. Only values that differ from the shipped defaults are written to the user file,
/// so an improved default (a better system prompt, a newer model) still reaches people who never
/// touched that setting.
/// </para>
/// <para>
/// An install made by the scripts in <c>tools/CuteBrowser/scripts</c> before the split kept its
/// settings in the shipped file. Those are still read, as defaults, so nothing is lost.
/// </para>
/// <para>
/// API keys fall back to environment variables. A blank key in the file means "look at the
/// environment"; a filled one wins. That way a checked-out copy of this repository never needs to
/// carry somebody's key, and a machine that has <c>OPENAI_API_KEY</c> set already works.
/// </para>
/// </remarks>
public sealed class BrowserSettings
{
    /// <summary>
    /// Names a settings file to use instead of the one in the application-data folder.
    /// </summary>
    /// <remarks>
    /// For a portable install on a USB stick, and for the tests, which must never write to the
    /// settings of the person running them.
    /// </remarks>
    public const string FileVariable = "CUTEBROWSER_CONFIG";

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase);

    private BrowserSettings(string? userFile) => UserFile = userFile;

    /// <summary>The settings for this process.</summary>
    public static BrowserSettings Current { get; } = Load(DefaultsFile, DefaultUserFile());

    /// <summary>The shipped defaults, beside the executable.</summary>
    public static string DefaultsFile
        => Path.Combine(AppContext.BaseDirectory, typeof(BrowserSettings).Assembly.GetName().Name + ".dll.config");

    /// <summary>Where <see cref="Save(out string)"/> writes, or null when there is nowhere.</summary>
    public string? UserFile { get; }

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
        var prefix = Prefix(provider);
        var (environment, model, endpoint) = provider switch
        {
            AiProvider.OpenAI => ("OPENAI_API_KEY", "gpt-4o", "https://api.openai.com/v1"),
            AiProvider.Anthropic => ("ANTHROPIC_API_KEY", "claude-sonnet-5", "https://api.anthropic.com/v1"),
            AiProvider.Gemini => ("GEMINI_API_KEY", "gemini-2.0-flash", "https://generativelanguage.googleapis.com/v1beta/openai/"),
            AiProvider.Compatible => ("OPENAI_COMPATIBLE_API_KEY", "deepseek-chat", "https://api.deepseek.com/v1"),
            AiProvider.AzureOpenAI => ("AZURE_OPENAI_API_KEY", "gpt-4o", "https://your-resource.openai.azure.com/"),
            _ => ("OLLAMA_API_KEY", "llama3.1", "http://localhost:11434/v1"),
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
        // The prefix comes from the same table ProfileFor reads with. Deriving it from the enum
        // name instead wrote Azure's details under "azureopenai.*" while they were read from
        // "azure.*", so a deployment typed into Settings was saved and then never seen again.
        var prefix = Prefix(profile.Provider);
        Set($"{prefix}.model", profile.Model);
        Set($"{prefix}.apiKey", profile.ApiKey);
        Set($"{prefix}.endpoint", profile.Endpoint);
    }

    private static string Prefix(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "openai",
        AiProvider.Anthropic => "anthropic",
        AiProvider.Gemini => "gemini",
        AiProvider.Compatible => "compatible",
        AiProvider.AzureOpenAI => "azure",
        _ => "ollama",
    };

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
    /// Writes the settings that differ from the shipped defaults to <see cref="UserFile"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A setting put back to its default is removed from the file rather than written out again,
    /// so the file only ever holds what the person actually chose.
    /// </para>
    /// <para>
    /// A failure here is reported rather than thrown: a read-only home directory is a real
    /// situation, and losing the settings you just typed is worse than the app forgetting them
    /// after you close it. The return value says which happened.
    /// </para>
    /// </remarks>
    public bool Save(out string message)
    {
        try
        {
            if (UserFile is null)
            {
                throw new InvalidOperationException(
                    "There is no application-data folder to save settings in. Set "
                    + FileVariable + " to a file path to choose one.");
            }

            if (Path.GetDirectoryName(UserFile) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            var configuration = Open(UserFile);
            var stored = configuration.AppSettings.Settings;

            foreach (var (key, value) in _values)
            {
                var isDefault = _defaults.TryGetValue(key, out var shipped)
                    ? string.Equals(shipped, value, StringComparison.Ordinal)
                    : value.Length == 0;

                if (isDefault)
                {
                    stored.Remove(key);
                }
                else if (stored[key] is null)
                {
                    stored.Add(key, value);
                }
                else
                {
                    stored[key].Value = value;
                }
            }

            // Full rather than Modified: the file may not exist yet, and a first save must write
            // the section even when every value in it is new.
            configuration.Save(ConfigurationSaveMode.Full);

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

    /// <summary>
    /// Reads the shipped defaults, then the person's own settings over them.
    /// </summary>
    /// <param name="defaultsFile">The shipped <c>app.config</c>. A missing file means no defaults.</param>
    /// <param name="userFile">Where saves go. A missing file means nothing has been saved yet.</param>
    public static BrowserSettings Load(string defaultsFile, string? userFile)
    {
        var settings = new BrowserSettings(userFile);

        // Read through the mapped-file API rather than ConfigurationManager.AppSettings: the latter
        // reads whichever .config belongs to the entry assembly, which under a test host is not
        // this one, and caches what it read.
        foreach (var (key, value) in Read(defaultsFile))
        {
            settings._defaults[key] = value;
            settings._values[key] = value;
        }

        // When there is no application-data folder the user file is the defaults file, and
        // reading it twice is harmless.
        if (userFile is not null)
        {
            foreach (var (key, value) in Read(userFile))
            {
                settings._values[key] = value;
            }
        }

        return settings;
    }

    /// <summary>
    /// Where this person's settings live: <see cref="FileVariable"/> if it is set, otherwise
    /// <c>CuteDB/Browser/app.config</c> under the application-data folder.
    /// </summary>
    /// <remarks>
    /// That folder is <c>%APPDATA%</c> on Windows and <c>~/.config</c> (or
    /// <c>$XDG_CONFIG_HOME</c>) elsewhere. When there is none — a service account, a container with
    /// no home directory — the file beside the executable is used, which is where every version
    /// before the split kept its settings.
    /// </remarks>
    public static string? DefaultUserFile()
    {
        var explicitFile = Environment.GetEnvironmentVariable(FileVariable);
        if (!string.IsNullOrWhiteSpace(explicitFile))
        {
            return Path.GetFullPath(explicitFile);
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData)
            ? DefaultsFile
            : Path.Combine(appData, "CuteDB", "Browser", "app.config");
    }

    private static Configuration Open(string file)
        => ConfigurationManager.OpenMappedExeConfiguration(
            new ExeConfigurationFileMap { ExeConfigFilename = file },
            ConfigurationUserLevel.None);

    private static List<(string Key, string Value)> Read(string file)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        var settings = Open(file).AppSettings.Settings;
        return [.. settings.AllKeys.Select(key => (key, settings[key].Value ?? string.Empty))];
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
