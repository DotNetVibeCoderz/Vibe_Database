using System.Text;
using System.Text.RegularExpressions;
using CuteDB.Browser.Ai;
using CuteDB.Browser.Ai.Plugins;
using CuteDB.Browser.Services;
using Microsoft.SemanticKernel;

namespace CuteDB.Browser.Tests;

/// <summary>
/// The tools Jack is offered, built exactly as <see cref="JackAgent"/> builds them.
/// </summary>
/// <remarks>
/// <para>
/// A tool schema is read by a model, not a compiler. A function with no description is one the
/// model calls blindly or not at all; a parameter with no description is one it fills with a guess;
/// a duplicate or over-long name is rejected outright by some providers and quietly dropped by
/// others. None of that shows until someone asks a question and the answer is worse.
/// </para>
/// <para>
/// The second half proves the promise in the docs: Jack does not run writes. Every tool is called
/// with every kind of write as every string argument, and the database must come out byte-for-byte
/// what it went in.
/// </para>
/// </remarks>
public sealed partial class JackToolTests : IDisposable
{
    private readonly Workspace _workspace = TestEnvironment.Seeded();
    private readonly WebPlugin _web;
    private readonly Kernel _kernel;

    public JackToolTests()
    {
        // Web tools on, so their schemas are checked too; with no Tavily key and the network
        // never reached by the write test (see below), nothing leaves the machine.
        var settings = BrowserSettings.Load(TestEnvironment.ShippedConfig, TestEnvironment.NewPath(".config"));
        settings.WebToolsEnabled = true;

        _web = new WebPlugin(settings, _workspace.Log);
        _kernel = BuildKernel(settings);
    }

    public void Dispose()
    {
        _web.Dispose();
        _workspace.Close();
    }

    private Kernel BuildKernel(BrowserSettings settings)
    {
        var builder = Kernel.CreateBuilder();
        JackAgent.AddTools(builder.Plugins, _workspace, settings, _workspace.Log, _web);
        return builder.Build();
    }

    private IEnumerable<KernelFunction> Functions => _kernel.Plugins.SelectMany(p => p);

    [Fact]
    public void The_documented_tools_are_all_there()
    {
        // docs/en/browser.md lists these, in this grouping. A renamed function would leave the
        // docs describing a tool the model is never offered.
        var expected = new Dictionary<string, string[]>
        {
            ["cutedb"] = ["list_collections", "describe_collection", "preview_query", "validate_cuteql", "explain_query", "list_indexes", "database_stats"],
            ["toolbox"] = ["math_calculate", "math_summarise", "current_datetime", "date_maths", "encode_text"],
            ["web"] = ["search_internet", "scrape_web_page"],
        };

        Assert.Equal(expected.Keys.Order(), _kernel.Plugins.Select(p => p.Name).Order());

        foreach (var (plugin, functions) in expected)
        {
            Assert.Equal(functions.Order(), _kernel.Plugins[plugin].Select(f => f.Name).Order());
        }
    }

    [Fact]
    public void Web_tools_are_not_offered_when_switched_off()
    {
        var settings = BrowserSettings.Load(TestEnvironment.ShippedConfig, TestEnvironment.NewPath(".config"));
        settings.WebToolsEnabled = false;

        var kernel = BuildKernel(settings);

        Assert.DoesNotContain(kernel.Plugins, p => p.Name == "web");
        Assert.Contains(kernel.Plugins, p => p.Name == "cutedb");
    }

    [Fact]
    public void Every_function_has_a_description_a_model_can_act_on()
    {
        Assert.All(Functions, function =>
        {
            Assert.False(string.IsNullOrWhiteSpace(function.Description), $"{function.Name} has no description.");
            Assert.True(function.Description.Length >= 40, $"{function.Name}'s description is too thin to choose by.");
            Assert.EndsWith(".", function.Description.TrimEnd());
        });
    }

    [Fact]
    public void Every_parameter_has_a_type_and_a_description()
    {
        Assert.All(Functions, function =>
        {
            foreach (var parameter in function.Metadata.Parameters)
            {
                var where = $"{function.Name}({parameter.Name})";

                Assert.False(string.IsNullOrWhiteSpace(parameter.Name), where);
                Assert.False(string.IsNullOrWhiteSpace(parameter.Description), $"{where} has no description.");
                Assert.NotNull(parameter.ParameterType);
                Assert.NotNull(parameter.Schema);

                // The schema is what goes over the wire; it has to say what type to send.
                Assert.Contains("\"type\"", parameter.Schema!.ToString());
            }
        });
    }

    [Fact]
    public void The_cancellation_token_is_not_offered_to_the_model()
    {
        Assert.DoesNotContain(
            Functions.SelectMany(f => f.Metadata.Parameters),
            p => p.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void Names_are_unique_and_valid_for_every_provider()
    {
        // Semantic Kernel sends "plugin-function" to OpenAI-shaped APIs, and Anthropic's
        // AnthropicChatCompletionService uses the same. Both allow [A-Za-z0-9_-] up to 64.
        var qualified = Functions.Select(f => $"{f.PluginName}-{f.Name}").ToList();

        Assert.Equal(qualified.Count, qualified.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(qualified, name => Assert.Matches(ToolName(), name));

        // Bare names must be unique too: the system prompt and the descriptions refer to tools by
        // their bare name ("call list_collections first"), and an ambiguity there is one the
        // model resolves by guessing.
        var bare = Functions.Select(f => f.Name).ToList();
        Assert.Equal(bare.Count, bare.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Tools_named_in_the_system_prompt_exist()
    {
        var settings = BrowserSettings.Load(TestEnvironment.ShippedConfig, TestEnvironment.NewPath(".config"));
        var names = Functions.Select(f => f.Name).ToHashSet();

        foreach (Match match in SnakeCase().Matches(settings.SystemPrompt))
        {
            Assert.True(names.Contains(match.Value), $"The system prompt mentions '{match.Value}', which is not a tool.");
        }
    }

    // ---- writes ------------------------------------------------------------------------------

    private static readonly string[] Writes =
    [
        "DELETE FROM orders",
        "DELETE FROM orders WHERE code = 'SO-001'",
        "UPDATE orders SET total = 0",
        "INSERT INTO orders VALUES { 'code': 'SO-999', 'total': 1 }",
        "INSERT INTO jack_was_here VALUES { 'x': 1 }",
        "SELECT * FROM orders; DELETE FROM orders",
        "SELECT * FROM orders; UPDATE products SET price = 0;",
        "DELETE FROM products; SELECT * FROM products",
        "-- harmless\nDELETE FROM customers",
        "/* select */ UPDATE customers SET tier = 'none'",
    ];

    [Fact]
    public async Task No_tool_can_change_the_database_whatever_it_is_given()
    {
        var before = Snapshot();

        foreach (var function in Functions.Where(f => f.PluginName != "web"))
        {
            var strings = function.Metadata.Parameters.Where(p => p.ParameterType == typeof(string)).ToList();

            foreach (var write in Writes)
            {
                var arguments = new KernelArguments();
                foreach (var parameter in function.Metadata.Parameters)
                {
                    arguments[parameter.Name] = parameter.ParameterType == typeof(string) ? write : parameter.DefaultValue;
                }

                var result = await function.InvokeAsync(_kernel, arguments);
                Assert.NotNull(result.ToString());

                Assert.True(before == Snapshot(), $"{function.Name}(\"{write}\") changed the database.");

                if (strings.Count == 0)
                {
                    // A function with no text input gets nothing to vary; once is enough.
                    break;
                }
            }
        }
    }

    [Fact]
    public async Task The_web_tools_take_no_database_input_at_all()
    {
        // The web plugin is left out of the write sweep above because calling it would reach the
        // network. It needs no sweep: it is constructed without the workspace, so it has no
        // reference through which a write could happen.
        Assert.DoesNotContain(
            typeof(WebPlugin).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(Workspace) || p.ParameterType == typeof(CuteDatabase));

        // And with the switch off it declines before touching the network.
        var settings = BrowserSettings.Load(TestEnvironment.ShippedConfig, TestEnvironment.NewPath(".config"));
        settings.WebToolsEnabled = false;
        using var off = new WebPlugin(settings, _workspace.Log);

        Assert.Equal("Web tools are switched off in settings.", await off.ScrapeWebPageAsync("https://example.com"));
        Assert.Equal("Web tools are switched off in settings.", await off.SearchInternetAsync("anything"));
    }

    [Fact]
    public async Task Preview_refuses_a_write_and_says_why()
    {
        var preview = _kernel.Plugins["cutedb"]["preview_query"];

        var refused = (await preview.InvokeAsync(_kernel, new() { ["cuteql"] = "DELETE FROM orders" })).ToString();
        Assert.Contains("only run SELECT", refused);

        var several = (await preview.InvokeAsync(_kernel, new() { ["cuteql"] = "SELECT * FROM orders; DELETE FROM orders" })).ToString();
        Assert.Contains("exactly one statement", several);

        var select = (await preview.InvokeAsync(_kernel, new() { ["cuteql"] = "SELECT code FROM orders WHERE address.city = 'Bandung'" })).ToString();
        Assert.StartsWith("2 rows", select);
    }

    [Fact]
    public async Task The_read_tools_answer_from_the_real_database()
    {
        var list = (await _kernel.InvokeAsync("cutedb", "list_collections")).ToString();
        Assert.Contains("orders: 7 documents", list);
        Assert.Contains("address.city", list);

        var describe = (await _kernel.InvokeAsync("cutedb", "describe_collection", new() { ["collection"] = "orders" })).ToString();
        Assert.Contains("address.city", describe);
        Assert.Contains("lines", describe);

        var validate = (await _kernel.InvokeAsync("cutedb", "validate_cuteql", new() { ["cuteql"] = "SELECT * FROM orders WHERE address.city = 'Medan'; SELEC nonsense" })).ToString();
        Assert.Contains("OK   SELECT", validate);
        Assert.Contains("FAIL SELEC nonsense", validate);
    }

    /// <summary>
    /// Everything that a write could change: collection names, every document, every index.
    /// </summary>
    private string Snapshot()
    {
        var database = _workspace.Require();
        var text = new StringBuilder();

        foreach (var name in database.CollectionNames.Order(StringComparer.Ordinal))
        {
            var collection = database.Collection(name);
            text.Append('#').Append(name).Append(' ').Append(collection.Count).AppendLine();

            foreach (var index in collection.Indexes.OrderBy(i => i.Path, StringComparer.Ordinal))
            {
                text.Append("  index ").Append(index.Path).Append(' ').Append(index.EntryCount).AppendLine();
            }

            foreach (var document in collection.All().Select(d => d.ToJson(indented: false)).Order(StringComparer.Ordinal))
            {
                text.AppendLine(document);
            }
        }

        return text.ToString();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ToolName();

    // A tool name as the prompt writes one: lower case with at least one underscore.
    [GeneratedRegex(@"\b[a-z]+(?:_[a-z]+)+\b")]
    private static partial Regex SnakeCase();
}
