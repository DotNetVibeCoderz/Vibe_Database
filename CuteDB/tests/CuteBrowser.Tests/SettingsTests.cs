using System.Security.Cryptography;
using CuteDB.Browser.Services;

namespace CuteDB.Browser.Tests;

/// <summary>
/// <see cref="BrowserSettings"/>: written by Tools ▸ Settings, read back on the next launch.
/// </summary>
/// <remarks>
/// Every test copies the shipped <c>App.config</c> to a temporary directory and points the user
/// file there too. Nothing here reads or writes the real settings of whoever runs the tests.
/// </remarks>
public sealed class SettingsTests
{
    private readonly string _defaults;
    private readonly string _user;

    public SettingsTests()
    {
        var directory = TestEnvironment.NewPath(string.Empty);
        Directory.CreateDirectory(directory);

        _defaults = Path.Combine(directory, "CuteBrowser.dll.config");
        File.Copy(TestEnvironment.ShippedConfig, _defaults);

        // A directory that does not exist yet, as on a first launch.
        _user = Path.Combine(directory, "appdata", "CuteDB", "Browser", "app.config");
    }

    private BrowserSettings Load() => BrowserSettings.Load(_defaults, _user);

    [Fact]
    public void The_shipped_file_is_read_as_the_defaults()
    {
        var settings = Load();

        Assert.Equal(AiProvider.OpenAI, settings.Provider);
        Assert.Equal(0.2, settings.Temperature);
        Assert.Equal(24, settings.HistoryTurns);
        Assert.Equal(500, settings.ResultsPageSize);
        Assert.StartsWith("You are Jack", settings.SystemPrompt);
        Assert.Equal("deepseek-chat", settings.ProfileFor(AiProvider.Compatible).Model);
        Assert.False(File.Exists(_user));
    }

    [Fact]
    public void Every_setting_round_trips_through_the_file()
    {
        var settings = Load();

        settings.Provider = AiProvider.AzureOpenAI;
        settings.Temperature = 0.75;
        settings.HistoryTurns = 9;
        settings.MaxToolCalls = 3;
        settings.SystemPrompt = "Line one — with an em dash.\nLine two has <angle> & \"quotes\" and 'apostrophes'.\n\tTabbed.";
        settings.TavilyKey = "tvly-test";
        settings.WebToolsEnabled = false;
        settings.ShowLineNumbers = false;
        settings.EditorFontSize = 15.5;
        settings.WordWrap = true;
        settings.ResultsPageSize = 1234;
        settings.ExplorerWidth = 321;
        settings.ChatVisible = false;
        settings.ChatWidth = 444;
        settings.LogsVisible = false;
        settings.LogsHeight = 222;
        settings.Remember(Path.Combine("data", "b.cute"));
        settings.Remember(Path.Combine("data", "a.cute"));

        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            settings.UpdateProfile(new ProviderProfile(provider, $"{provider}-model", $"{provider}-key", $"https://{provider}.example/v1"));
        }

        Assert.True(settings.Save(out var message), message);
        Assert.Equal(_user, message);

        var reloaded = Load();

        Assert.Equal(AiProvider.AzureOpenAI, reloaded.Provider);
        Assert.Equal(0.75, reloaded.Temperature);
        Assert.Equal(9, reloaded.HistoryTurns);
        Assert.Equal(3, reloaded.MaxToolCalls);
        Assert.Equal(settings.SystemPrompt, reloaded.SystemPrompt);
        Assert.Equal("tvly-test", reloaded.TavilyKey);
        Assert.False(reloaded.WebToolsEnabled);
        Assert.False(reloaded.ShowLineNumbers);
        Assert.Equal(15.5, reloaded.EditorFontSize);
        Assert.True(reloaded.WordWrap);
        Assert.Equal(1234, reloaded.ResultsPageSize);
        Assert.Equal(321, reloaded.ExplorerWidth);
        Assert.False(reloaded.ChatVisible);
        Assert.Equal(444, reloaded.ChatWidth);
        Assert.False(reloaded.LogsVisible);
        Assert.Equal(222, reloaded.LogsHeight);
        Assert.Equal([Path.Combine("data", "a.cute"), Path.Combine("data", "b.cute")], reloaded.Recent);
        Assert.Equal(Path.Combine("data", "a.cute"), reloaded.LastDatabase);

        // Every provider, including Azure — whose details were once written under "azureopenai.*"
        // and read from "azure.*", so they were saved and never seen again.
        foreach (var provider in Enum.GetValues<AiProvider>())
        {
            Assert.Equal(settings.ProfileFor(provider), reloaded.ProfileFor(provider));
            Assert.Equal($"{provider}-model", reloaded.ProfileFor(provider).Model);
        }
    }

    [Fact]
    public void Saving_never_writes_to_the_shipped_file()
    {
        // The shipped file lives in the tool store when installed with `dotnet tool`. Writing there
        // would be lost on the next update, or refused outright on a machine-wide install.
        var before = SHA256.HashData(File.ReadAllBytes(_defaults));

        var settings = Load();
        settings.Temperature = 0.9;
        Assert.True(settings.Save(out var message), message);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(_defaults)));
        Assert.True(File.Exists(_user));
    }

    [Fact]
    public void Only_changed_settings_are_written_so_new_defaults_still_arrive()
    {
        var settings = Load();
        settings.Temperature = 0.5;
        Assert.True(settings.Save(out var message), message);

        var written = File.ReadAllText(_user);
        Assert.Contains("ai.temperature", written);
        Assert.DoesNotContain("ai.systemPrompt", written);
        Assert.DoesNotContain("openai.model", written);

        // A later release ships a different default. Someone who never touched it gets the new one;
        // the setting they did change stays theirs.
        File.WriteAllText(_defaults, File.ReadAllText(_defaults)
            .Replace("key=\"openai.model\" value=\"gpt-4o\"", "key=\"openai.model\" value=\"gpt-next\"", StringComparison.Ordinal));

        var updated = Load();
        Assert.Equal("gpt-next", updated.ProfileFor(AiProvider.OpenAI).Model);
        Assert.Equal(0.5, updated.Temperature);
    }

    [Fact]
    public void Putting_a_setting_back_to_its_default_removes_it_from_the_file()
    {
        var settings = Load();
        settings.HistoryTurns = 3;
        Assert.True(settings.Save(out _));
        Assert.Contains("ai.historyTurns", File.ReadAllText(_user));

        var again = Load();
        again.HistoryTurns = 24;
        Assert.True(again.Save(out _));

        Assert.DoesNotContain("ai.historyTurns", File.ReadAllText(_user));
        Assert.Equal(24, Load().HistoryTurns);
    }

    [Fact]
    public void Clearing_a_key_that_the_shipped_file_filled_in_is_remembered()
    {
        // An install made by the old scripts kept keys in the shipped file. Clearing one in the
        // dialog has to stick, which means writing the empty value rather than dropping the key.
        File.WriteAllText(_defaults, File.ReadAllText(_defaults)
            .Replace("key=\"tavily.apiKey\" value=\"\"", "key=\"tavily.apiKey\" value=\"tvly-old\"", StringComparison.Ordinal));

        var settings = Load();
        Assert.Equal("tvly-old", settings.Get("tavily.apiKey"));

        settings.TavilyKey = string.Empty;
        Assert.True(settings.Save(out _));

        Assert.Equal(string.Empty, Load().Get("tavily.apiKey"));
    }

    [Fact]
    public void A_missing_shipped_file_falls_back_to_the_built_in_values()
    {
        var settings = BrowserSettings.Load(TestEnvironment.NewPath(".config"), TestEnvironment.NewPath(".config"));

        Assert.Equal(AiProvider.OpenAI, settings.Provider);
        Assert.Equal(0.2, settings.Temperature);
        Assert.True(settings.WebToolsEnabled);
        Assert.Equal("gpt-4o", settings.ProfileFor(AiProvider.AzureOpenAI).Model);
    }

    [Fact]
    public void A_save_that_cannot_be_written_is_reported_not_thrown()
    {
        var blocker = TestEnvironment.NewPath(".file");
        File.WriteAllText(blocker, "a file where a directory has to go");

        var settings = BrowserSettings.Load(_defaults, Path.Combine(blocker, "app.config"));
        settings.Temperature = 0.4;

        Assert.False(settings.Save(out var message));
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void The_user_file_can_be_redirected_by_environment_variable()
    {
        // The module initializer set it before anything ran, which is also what keeps
        // BrowserSettings.Current off the developer's own settings during these tests.
        var expected = Path.Combine(TestEnvironment.Root, "current.config");

        Assert.Equal(expected, BrowserSettings.DefaultUserFile());
        Assert.Equal(expected, BrowserSettings.Current.UserFile);
    }
}
