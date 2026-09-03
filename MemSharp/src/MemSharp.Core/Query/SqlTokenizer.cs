using System.Text;

namespace MemSharp.Query;

internal enum TokenKind
{
    Identifier,
    String,
    Number,
    Operator,
    Comma,
    OpenParen,
    CloseParen,
    Star,
    End,
}

internal readonly record struct Token(TokenKind Kind, string Text, int Position)
{
    public bool Is(string keyword) => Kind == TokenKind.Identifier && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
    public override string ToString() => Kind == TokenKind.End ? "end of query" : $"'{Text}'";
}

/// <summary>
/// Splits a query into tokens.
/// </summary>
/// <remarks>
/// A hand-written scanner rather than a regex. The engine's original SQL layer was one
/// <c>Regex.Match</c> per query, which compiled a state machine, allocated a match object and a
/// group collection for every call, and still could not express anything beyond the one shape it
/// was written for. This walks the string once with no allocation except the token text itself.
/// </remarks>
internal ref struct SqlTokenizer
{
    private readonly ReadOnlySpan<char> _text;
    private int _position;

    public SqlTokenizer(ReadOnlySpan<char> text)
    {
        _text = text;
        _position = 0;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = Next();
            tokens.Add(token);
            if (token.Kind == TokenKind.End) return tokens;
        }
    }

    private Token Next()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        if (_position >= _text.Length) return new Token(TokenKind.End, string.Empty, _position);

        int start = _position;
        char c = _text[_position];

        switch (c)
        {
            case ',': _position++; return new Token(TokenKind.Comma, ",", start);
            case '(': _position++; return new Token(TokenKind.OpenParen, "(", start);
            case ')': _position++; return new Token(TokenKind.CloseParen, ")", start);
            case '*': _position++; return new Token(TokenKind.Star, "*", start);
            case '\'' or '"': return ReadString(c);
        }

        if (c is '=' or '<' or '>' or '!')
        {
            _position++;
            // Two-character comparisons: <=, >=, !=, <>. Checking for the second character here
            // rather than in the parser keeps '<' and '<=' from being ambiguous downstream.
            if (_position < _text.Length && _text[_position] == '=')
            {
                _position++;
                return new Token(TokenKind.Operator, _text[start.._position].ToString(), start);
            }
            if (c == '<' && _position < _text.Length && _text[_position] == '>')
            {
                _position++;
                return new Token(TokenKind.Operator, "!=", start);
            }
            return new Token(TokenKind.Operator, c.ToString(), start);
        }

        if (char.IsDigit(c) || (c == '-' && _position + 1 < _text.Length && char.IsDigit(_text[_position + 1])))
        {
            _position++;
            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] == '.')) _position++;
            return new Token(TokenKind.Number, _text[start.._position].ToString(), start);
        }

        if (char.IsLetter(c) || c == '_')
        {
            while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] is '_' or '.' or ':'))
            {
                _position++;
            }
            return new Token(TokenKind.Identifier, _text[start.._position].ToString(), start);
        }

        throw new MemSharpCommandException($"unexpected character '{c}' at position {start}");
    }

    private Token ReadString(char quote)
    {
        int start = _position;
        _position++;   // opening quote

        var builder = new StringBuilder();
        while (_position < _text.Length)
        {
            char c = _text[_position];

            if (c == '\\' && _position + 1 < _text.Length)
            {
                // Backslash escapes exist so a pattern can contain a literal quote. Anything else
                // after a backslash is passed through unchanged - the glob matcher has its own
                // escape rules and must see them intact.
                _position++;
                builder.Append(_text[_position++]);
                continue;
            }

            if (c == quote)
            {
                _position++;
                if (_position < _text.Length && _text[_position] == quote)
                {
                    builder.Append(quote);   // SQL-style doubled quote
                    _position++;
                    continue;
                }
                return new Token(TokenKind.String, builder.ToString(), start);
            }

            builder.Append(c);
            _position++;
        }

        throw new MemSharpCommandException($"unterminated string literal starting at position {start}");
    }
}
