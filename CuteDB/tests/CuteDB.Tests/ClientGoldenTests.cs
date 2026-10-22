using System.Text.Json;
using System.Text.RegularExpressions;
using CuteDB.Query;

namespace CuteDB.Tests;

/// <summary>
/// Holds the client query builders to the real parser and the real engine.
/// </summary>
/// <remarks>
/// The Python, Go and Node builders each assert that they render exactly the CuteQL and parameters
/// in <c>clients/querybuilder-golden.json</c>. That proves the three agree with each other; it
/// does not prove any of them is right. This does: every golden statement is parsed here, rendered
/// back by <see cref="CuteQLWriter"/>, and run against the golden fixture with its parameters read
/// the way the server reads them, and the rows that come back have to be the ones the file names.
/// </remarks>
public sealed partial class ClientGoldenTests
{
    private static readonly Lazy<JsonDocument> Golden = new(() =>
        JsonDocument.Parse(File.ReadAllText(FindGolden())));

    private static readonly Lazy<CuteDatabase> Database = new(Seed);

    public static TheoryData<string> CaseNames()
    {
        var names = new TheoryData<string>();
        foreach (var item in Golden.Value.RootElement.GetProperty("cases").EnumerateArray())
        {
            names.Add(item.GetProperty("name").GetString()!);
        }

        return names;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void GoldenStatementParsesAndReturnsTheNamedRows(string name)
    {
        var item = Golden.Value.RootElement.GetProperty("cases").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == name);
        var text = item.GetProperty("cuteql").GetString()!;

        // Canonical: the writer renders the parsed statement back to the same text. The one
        // spelling it normalises is `IN @p`, which the builders emit because it is the documented
        // form for a parameter holding an array and the writer brackets as `IN (@p)`.
        var statement = CuteParser.ParseStatement(text);
        Assert.Equal(InParameter().Replace(text, " IN (@$1)"), CuteQLWriter.Write(statement));

        // Every placeholder is bound and every binding is used — an unbound one would quietly
        // resolve to MISSING and match nothing.
        var parameters = new CuteParameters();
        foreach (var bound in item.GetProperty("parameters").EnumerateObject())
        {
            parameters.Set(bound.Name, CuteJson.Parse(bound.Value.GetRawText(), CuteJsonOptions.Financial));
        }

        var placeholders = Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        var names = item.GetProperty("parameters").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(names.Order(), placeholders.Order());

        var result = Database.Value.Execute(text, parameters);
        var actual = result.Rows.Select(r => r["code"].AsString).ToList();
        var expected = item.GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToList();

        if (item.TryGetProperty("ordered", out var ordered) && ordered.GetBoolean())
        {
            Assert.Equal(expected, actual);
        }
        else
        {
            Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
        }
    }

    private static CuteDatabase Seed()
    {
        var database = CuteDatabase.CreateInMemory();
        var root = Golden.Value.RootElement;
        var collection = database.Collection(root.GetProperty("collection").GetString()!);

        // Read through CuteJson with the server's options, so `{"$date": …}` becomes a datetime and
        // 125000.5 a decimal exactly as they would arriving over HTTP.
        foreach (var document in root.GetProperty("fixture").EnumerateArray())
        {
            collection.Insert(CuteJson.Parse(document.GetRawText(), CuteJsonOptions.Financial).AsObject);
        }

        return database;
    }

    private static string FindGolden()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "clients", "querybuilder-golden.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("clients/querybuilder-golden.json was not found above the test output.");
    }

    [GeneratedRegex(@" IN @(p\d+)")]
    private static partial Regex InParameter();

    [GeneratedRegex(@"@(p\d+)")]
    private static partial Regex Placeholder();
}
