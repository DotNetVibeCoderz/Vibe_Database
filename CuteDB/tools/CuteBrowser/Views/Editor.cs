using System.Text;
using System.Xml;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using CuteDB.Browser.Services;
using CuteDB.Query;

namespace CuteDB.Browser.Views;

/// <summary>
/// Building and configuring the text editor a query tab is written in.
/// </summary>
/// <remarks>
/// AvaloniaEdit brings the line-number margin, the folding-free simple layout and a highlighting
/// engine that reads the same XSHD files SharpDevelop used. C# highlighting ships with it; CuteQL
/// does not exist as far as it is concerned, so the definition is loaded from an embedded resource
/// once and registered under its own name.
/// </remarks>
internal static class Editor
{
    private const string CuteQLName = "CuteQL";
    private const string CSharpName = "CuteCSharp";
    private static bool _registered;

    /// <summary>Creates an editor set up for a language.</summary>
    internal static TextEditor Create(QueryLanguage language, BrowserSettings settings)
    {
        Register();

        var editor = new TextEditor
        {
            ShowLineNumbers = settings.ShowLineNumbers,
            FontFamily = Mono(),
            FontSize = settings.EditorFontSize,
            Background = Ui.Brush("Nila"),
            Foreground = Ui.Brush("Lilin"),
            WordWrap = settings.WordWrap,
            Padding = new Thickness(10, 8),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 2;
        editor.Options.HighlightCurrentLine = true;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.ShowBoxForControlCharacters = false;

        editor.TextArea.TextView.LinkTextForegroundBrush = Ui.Brush("Kunyit");
        editor.TextArea.SelectionBrush = Ui.Brush("KunyitSoft");
        editor.TextArea.SelectionForeground = Ui.Brush("Lilin");
        editor.TextArea.Caret.CaretBrush = Ui.Brush("Kunyit");

        // The current-line highlight is a fill rather than a border: a border around the caret line
        // in a dark editor reads as a selection, which it is not.
        editor.TextArea.TextView.CurrentLineBackground = Ui.Brush("NilaSunk");
        editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);

        ApplyLanguage(editor, language);
        return editor;
    }

    /// <summary>Switches an editor between CuteQL and C#.</summary>
    internal static void ApplyLanguage(TextEditor editor, QueryLanguage language)
    {
        var name = language == QueryLanguage.CuteQL ? CuteQLName : CSharpName;

        // Falls back to the stock C# definition if ours failed to load, which is worse-looking but
        // still highlighted.
        editor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition(name)
            ?? HighlightingManager.Instance.GetDefinition("C#");
    }

    /// <summary>Applies the settings that can change while a tab is open.</summary>
    internal static void ApplySettings(TextEditor editor, BrowserSettings settings)
    {
        editor.ShowLineNumbers = settings.ShowLineNumbers;
        editor.FontSize = settings.EditorFontSize;
        editor.WordWrap = settings.WordWrap;
    }

    /// <summary>Puts the caret on a line and scrolls to it.</summary>
    internal static void GoToLine(TextEditor editor, int line)
    {
        var clamped = Math.Clamp(line, 1, editor.Document.LineCount);
        var offset = editor.Document.GetLineByNumber(clamped).Offset;

        editor.CaretOffset = offset;
        editor.ScrollToLine(clamped);
        editor.TextArea.Focus();
    }

    /// <summary>
    /// Reformats what is in the editor.
    /// </summary>
    /// <remarks>
    /// For CuteQL this is a real round trip — parse, then render through
    /// <see cref="CuteQLWriter"/> — so formatting also tells you the query is valid, and the result
    /// is the engine's own idea of what you wrote rather than a guess made with regular
    /// expressions. Anything that does not parse is left exactly as it was and the failure is
    /// reported; silently mangling text someone is in the middle of typing is the worst thing a
    /// format command can do.
    /// </remarks>
    internal static (bool Ok, string Message) Format(TextEditor editor, QueryLanguage language)
    {
        var text = editor.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return (false, "There is nothing to format.");
        }

        if (language != QueryLanguage.CuteQL)
        {
            var indented = IndentBraces(text);
            if (indented == text)
            {
                return (true, "Already tidy.");
            }

            editor.Text = indented;
            return (true, "Re-indented.");
        }

        var statements = QueryRunner.SplitStatements(text);
        var rendered = new List<string>(statements.Count);

        foreach (var statement in statements)
        {
            try
            {
                rendered.Add(CuteQLWriter.Write(CuteParser.ParseStatement(statement), indented: true));
            }
            catch (Exception exception)
            {
                return (false, $"Cannot format: {exception.Message}");
            }
        }

        var caret = editor.CaretOffset;
        editor.Text = string.Join(";" + Environment.NewLine + Environment.NewLine, rendered)
            + (statements.Count > 1 ? ";" : string.Empty);

        editor.CaretOffset = Math.Min(caret, editor.Document.TextLength);

        return (true, statements.Count == 1
            ? "Formatted."
            : $"Formatted {statements.Count} statements.");
    }

    /// <summary>Inserts text at the caret, or replaces the selection.</summary>
    internal static void Insert(TextEditor editor, string text)
    {
        if (editor.SelectionLength > 0)
        {
            editor.Document.Replace(editor.SelectionStart, editor.SelectionLength, text);
        }
        else
        {
            editor.Document.Insert(editor.CaretOffset, text);
        }

        editor.TextArea.Focus();
    }

    /// <summary>The text the Run button should run: the selection if there is one, else everything.</summary>
    /// <remarks>
    /// Running the selection is how every SQL tool works and is worth keeping: in a tab holding six
    /// statements, being able to run one is the difference between a scratchpad and a script.
    /// </remarks>
    internal static string RunnableText(TextEditor editor)
        => editor.SelectionLength > 0 ? editor.SelectedText : editor.Text;

    private static FontFamily Mono()
        => Ui.Resource("MonoFont") as FontFamily ?? new FontFamily("Consolas, monospace");

    private static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        Load("CuteDB.Browser.Theme.CuteQL.xshd", CuteQLName, [".cuteql", ".cql"]);
        Load("CuteDB.Browser.Theme.CSharp.xshd", CSharpName, [".csx", ".linq"]);
    }

    /// <summary>
    /// Registers one embedded highlighting definition.
    /// </summary>
    /// <remarks>
    /// A failure is swallowed. Highlighting is a nicety and an editor without it still edits;
    /// refusing to start the app because a resource name changed would not be a reasonable trade.
    /// </remarks>
    private static void Load(string resource, string name, string[] extensions)
    {
        try
        {
            using var stream = typeof(Editor).Assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                return;
            }

            using var reader = XmlReader.Create(stream);
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            HighlightingManager.Instance.RegisterHighlighting(name, extensions, definition);
        }
        catch (Exception)
        {
            // See above.
        }
    }

    /// <summary>
    /// A minimal C# re-indent: one level per unclosed brace.
    /// </summary>
    /// <remarks>
    /// Not a formatter. Roslyn could format properly, but a LINQ tab is a fragment rather than a
    /// compilation unit and Roslyn's formatter wants a parse tree it can trust. Fixing the
    /// indentation is the part people actually want from Format on a snippet, and it cannot be
    /// wrong in a way that loses code.
    /// </remarks>
    private static string IndentBraces(string text)
    {
        var builder = new StringBuilder(text.Length + 64);
        var depth = 0;

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith('}') || line.StartsWith(')'))
            {
                depth = Math.Max(0, depth - 1);
            }

            builder.Append(new string(' ', depth * 4)).AppendLine(line);

            var opens = line.Count(c => c is '{') - line.Count(c => c is '}');
            depth = Math.Max(0, depth + opens);
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }
}
