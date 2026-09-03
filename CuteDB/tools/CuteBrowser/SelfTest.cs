using CuteDB.Browser.Ai;
using CuteDB.Browser.Services;

namespace CuteDB.Browser;

/// <summary>
/// One turn with Jack, from the command line, with no window.
/// </summary>
/// <remarks>
/// <para>
/// <c>CuteBrowser --ask "..."</c> exists because an assistant is the one part of this app that
/// cannot be verified by looking at it. Whether the kernel is wired correctly, whether the provider
/// answers, whether a tool call actually reaches the database and comes back — none of that shows
/// in a screenshot, and all of it breaks quietly.
/// </para>
/// <para>
/// It uses the same <see cref="JackAgent"/>, the same plugins and the same settings the window
/// does, so a pass here means the panel will work. Add <c>--db &lt;path&gt;</c> to ask about a real
/// file; without one it seeds a temporary database from the Retail template, which is enough for
/// the tool calls to have something to find.
/// </para>
/// </remarks>
internal static class SelfTest
{
    /// <summary>Asks one question and prints the answer. Returns a process exit code.</summary>
    internal static async Task<int> AskAsync(string question, string? databasePath)
    {
        var log = new ActivityLog();
        var settings = BrowserSettings.Current;
        var workspace = new Workspace(log);

        var temporary = databasePath is null;
        var path = databasePath ?? Path.Combine(Path.GetTempPath(), $"cutebrowser-ask-{Guid.NewGuid():N}.cute");

        // Everything the log records goes to the console, so a tool call is visible as it happens
        // rather than inferred from the answer.
        log.Appended += entry => Console.WriteLine($"  [{entry.Source}] {entry.Message}");

        try
        {
            workspace.Open(path);

            if (temporary)
            {
                Templates.Apply(Templates.Databases.First(t => t.Name == "Retail"), workspace);
            }

            var profile = settings.ProfileFor(settings.Provider);
            Console.WriteLine($"provider : {profile.Label}");
            Console.WriteLine($"model    : {profile.Model}");
            Console.WriteLine($"endpoint : {profile.Endpoint}");
            Console.WriteLine($"key      : {(string.IsNullOrWhiteSpace(profile.ApiKey) ? "MISSING" : "present")}");
            Console.WriteLine($"database : {workspace.DisplayName} ({string.Join(", ", workspace.Collections())})");
            Console.WriteLine();
            Console.WriteLine($"> {question}");
            Console.WriteLine();

            using var jack = new JackAgent(workspace, settings, log);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            await jack.SendAsync(question, [], timeout.Token);

            var reply = jack.History.LastOrDefault();
            Console.WriteLine();
            Console.WriteLine(new string('-', 78));
            Console.WriteLine(reply?.Text ?? "(nothing came back)");
            Console.WriteLine(new string('-', 78));

            if (reply is null || reply.Role != ChatRole.Assistant)
            {
                Console.Error.WriteLine("FAILED: no assistant reply.");
                return 1;
            }

            foreach (var (language, code) in reply.CodeBlocks)
            {
                Console.WriteLine($"code block ({language}), {code.Length} chars");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAILED: {exception.Message}");
            return 1;
        }
        finally
        {
            workspace.Close();

            if (temporary)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A leftover temp file is not a test failure.
                }
            }
        }
    }
}
