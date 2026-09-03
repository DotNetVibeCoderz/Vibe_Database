using System.Globalization;
using System.Text;

namespace CuteDB.Query;

/// <summary>The kind of a CuteQL token.</summary>
public enum CuteTokenKind
{
    End,
    Identifier,
    Keyword,
    Number,
    String,
    Parameter,
    Comma,
    Dot,
    LeftParen,
    RightParen,
    LeftBracket,
    RightBracket,
    LeftBrace,
    RightBrace,
    Colon,
    Star,
    Plus,
    Minus,
    Slash,
    Percent,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>One lexed token, carrying its source offset so errors can point at it.</summary>
/// <param name="Kind">What sort of token this is.</param>
/// <param name="Text">The exact source text.</param>
/// <param name="Position">Offset of the token's first character.</param>
/// <param name="Value">The decoded value for literals.</param>
public readonly record struct CuteToken(CuteTokenKind Kind, string Text, int Position, CuteValue Value = default)
{
    /// <summary>True when this token is the given keyword, case-insensitively.</summary>
    public bool IsKeyword(string keyword)
        => Kind == CuteTokenKind.Keyword && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => Kind == CuteTokenKind.End ? "end of query" : $"'{Text}'";
}

/// <summary>
/// Turns CuteQL text into tokens.
/// </summary>
/// <remarks>
/// <para>
/// The dialect is SQL-shaped on purpose — anyone who has written a <c>WHERE</c> clause can use it
/// — with the concessions a document store has to make. Field paths are first-class, so
/// <c>customer.address.city</c> and <c>lines[0].sku</c> lex as single path tokens rather than as
/// an identifier and a pile of operators. Both <c>=</c> and <c>==</c> mean equality, because the
/// first is what SQL users type and the second is what everyone else does.
/// </para>
/// <para>
/// Strings accept single or double quotes. Single is the SQL spelling and doubles up to escape
/// (<c>'it''s'</c>); double-quoted strings use backslash escapes, matching JSON, so a JSON literal
/// can be pasted into a query unchanged.
/// </para>
/// </remarks>
public sealed class CuteLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "ORDER", "BY", "ASC", "DESC", "LIMIT", "OFFSET",
        "GROUP", "HAVING", "AS", "AND", "OR", "NOT", "IN", "LIKE", "BETWEEN", "IS",
        "NULL", "TRUE", "FALSE", "MISSING", "DELETE", "UPDATE", "SET", "INSERT", "INTO",
        "VALUES", "DISTINCT", "COUNT", "SUM", "AVG", "MIN", "MAX", "EXISTS",
    };

    private readonly string _source;
    private int _position;

    /// <summary>Creates a lexer over a query.</summary>
    public CuteLexer(string source) => _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Lexes the whole query, ending with an <see cref="CuteTokenKind.End"/> token.</summary>
    public List<CuteToken> Tokenize()
    {
        var tokens = new List<CuteToken>(32);
        while (true)
        {
            var token = Next();
            tokens.Add(token);
            if (token.Kind == CuteTokenKind.End)
            {
                return tokens;
            }
        }
    }

    private CuteToken Next()
    {
        SkipTrivia();
        if (_position >= _source.Length)
        {
            return new CuteToken(CuteTokenKind.End, string.Empty, _position);
        }

        var start = _position;
        var c = _source[_position];

        switch (c)
        {
            case ',': _position++; return new CuteToken(CuteTokenKind.Comma, ",", start);
            case '(': _position++; return new CuteToken(CuteTokenKind.LeftParen, "(", start);
            case ')': _position++; return new CuteToken(CuteTokenKind.RightParen, ")", start);
            case '[': _position++; return new CuteToken(CuteTokenKind.LeftBracket, "[", start);
            case ']': _position++; return new CuteToken(CuteTokenKind.RightBracket, "]", start);
            case '{': _position++; return new CuteToken(CuteTokenKind.LeftBrace, "{", start);
            case '}': _position++; return new CuteToken(CuteTokenKind.RightBrace, "}", start);
            case ':': _position++; return new CuteToken(CuteTokenKind.Colon, ":", start);
            case '*': _position++; return new CuteToken(CuteTokenKind.Star, "*", start);
            case '+': _position++; return new CuteToken(CuteTokenKind.Plus, "+", start);
            case '-': _position++; return new CuteToken(CuteTokenKind.Minus, "-", start);
            case '/': _position++; return new CuteToken(CuteTokenKind.Slash, "/", start);
            case '%': _position++; return new CuteToken(CuteTokenKind.Percent, "%", start);

            case '=':
                _position++;
                if (Peek() == '=')
                {
                    _position++;
                }

                return new CuteToken(CuteTokenKind.Equal, "=", start);

            case '!':
                _position++;
                if (Peek() != '=')
                {
                    throw Error("'!' has to be part of '!='.", start);
                }

                _position++;
                return new CuteToken(CuteTokenKind.NotEqual, "!=", start);

            case '<':
                _position++;
                if (Peek() == '=')
                {
                    _position++;
                    return new CuteToken(CuteTokenKind.LessOrEqual, "<=", start);
                }

                if (Peek() == '>')
                {
                    _position++;
                    return new CuteToken(CuteTokenKind.NotEqual, "<>", start);
                }

                return new CuteToken(CuteTokenKind.Less, "<", start);

            case '>':
                _position++;
                if (Peek() == '=')
                {
                    _position++;
                    return new CuteToken(CuteTokenKind.GreaterOrEqual, ">=", start);
                }

                return new CuteToken(CuteTokenKind.Greater, ">", start);

            case '\'':
                return ReadSqlString();

            case '"':
                return ReadJsonString();

            case '@':
            case '$':
                return ReadParameter();
        }

        if (char.IsAsciiDigit(c))
        {
            return ReadNumber();
        }

        if (char.IsLetter(c) || c == '_')
        {
            return ReadWord();
        }

        throw Error($"'{c}' does not belong in a query.", start);
    }

    private void SkipTrivia()
    {
        while (_position < _source.Length)
        {
            var c = _source[_position];
            if (char.IsWhiteSpace(c))
            {
                _position++;
                continue;
            }

            // -- line comment
            if (c == '-' && _position + 1 < _source.Length && _source[_position + 1] == '-')
            {
                while (_position < _source.Length && _source[_position] != '\n')
                {
                    _position++;
                }

                continue;
            }

            // /* block comment */
            if (c == '/' && _position + 1 < _source.Length && _source[_position + 1] == '*')
            {
                var end = _source.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                _position = end < 0 ? _source.Length : end + 2;
                continue;
            }

            return;
        }
    }

    private CuteToken ReadWord()
    {
        var start = _position;

        // A word may continue into a path: customer.address.city, or lines[0].sku. Consuming the
        // whole thing here keeps paths out of the parser's expression grammar, where '.' and '['
        // would otherwise collide with member access and array literals.
        while (_position < _source.Length)
        {
            var c = _source[_position];
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                _position++;
                continue;
            }

            if (c == '.' && _position + 1 < _source.Length &&
                (char.IsLetter(_source[_position + 1]) || _source[_position + 1] == '_'))
            {
                _position++;
                continue;
            }

            if (c == '[')
            {
                var close = _source.IndexOf(']', _position);
                if (close < 0)
                {
                    break;
                }

                var inner = _source.AsSpan(_position + 1, close - _position - 1).Trim();
                if (!inner.IsEmpty && !int.TryParse(inner, out _))
                {
                    break;
                }

                _position = close + 1;
                continue;
            }

            break;
        }

        var text = _source[start.._position];
        return Keywords.Contains(text) && text.IndexOf('.') < 0 && text.IndexOf('[') < 0
            ? new CuteToken(CuteTokenKind.Keyword, text, start)
            : new CuteToken(CuteTokenKind.Identifier, text, start);
    }

    private CuteToken ReadNumber()
    {
        var start = _position;
        var isFloating = false;

        while (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
        {
            _position++;
        }

        if (_position < _source.Length && _source[_position] == '.' &&
            _position + 1 < _source.Length && char.IsAsciiDigit(_source[_position + 1]))
        {
            isFloating = true;
            _position++;
            while (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
            {
                _position++;
            }
        }

        if (_position < _source.Length && (_source[_position] == 'e' || _source[_position] == 'E'))
        {
            var save = _position;
            _position++;
            if (_position < _source.Length && (_source[_position] == '+' || _source[_position] == '-'))
            {
                _position++;
            }

            if (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
            {
                isFloating = true;
                while (_position < _source.Length && char.IsAsciiDigit(_source[_position]))
                {
                    _position++;
                }
            }
            else
            {
                _position = save;
            }
        }

        var text = _source[start.._position];
        if (!isFloating)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i32))
            {
                return new CuteToken(CuteTokenKind.Number, text, start, CuteValue.Int32(i32));
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i64))
            {
                return new CuteToken(CuteTokenKind.Number, text, start, CuteValue.Int64(i64));
            }
        }

        return new CuteToken(
            CuteTokenKind.Number,
            text,
            start,
            CuteValue.Double(double.Parse(text, CultureInfo.InvariantCulture)));
    }

    private CuteToken ReadSqlString()
    {
        var start = _position++;
        var builder = new StringBuilder();

        while (true)
        {
            if (_position >= _source.Length)
            {
                throw Error("This string never closes.", start);
            }

            var c = _source[_position++];
            if (c != '\'')
            {
                builder.Append(c);
                continue;
            }

            // '' inside a single-quoted string is one literal quote, as in SQL.
            if (_position < _source.Length && _source[_position] == '\'')
            {
                builder.Append('\'');
                _position++;
                continue;
            }

            break;
        }

        var text = builder.ToString();
        return new CuteToken(CuteTokenKind.String, text, start, CuteValue.String(text));
    }

    private CuteToken ReadJsonString()
    {
        var start = _position++;
        var builder = new StringBuilder();

        while (true)
        {
            if (_position >= _source.Length)
            {
                throw Error("This string never closes.", start);
            }

            var c = _source[_position++];
            if (c == '"')
            {
                break;
            }

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (_position >= _source.Length)
            {
                throw Error("This string ends with a dangling escape.", start);
            }

            var escape = _source[_position++];
            builder.Append(escape switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                'b' => '\b',
                'f' => '\f',
                '0' => '\0',
                '\\' => '\\',
                '"' => '"',
                '\'' => '\'',
                '/' => '/',
                'u' => ReadUnicodeEscape(start),
                _ => throw Error($"'\\{escape}' is not an escape sequence.", _position - 2),
            });
        }

        var text = builder.ToString();
        return new CuteToken(CuteTokenKind.String, text, start, CuteValue.String(text));
    }

    private char ReadUnicodeEscape(int stringStart)
    {
        if (_position + 4 > _source.Length ||
            !ushort.TryParse(_source.AsSpan(_position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            throw Error(@"A \u escape needs four hex digits.", stringStart);
        }

        _position += 4;
        return (char)code;
    }

    private CuteToken ReadParameter()
    {
        var start = _position++;
        while (_position < _source.Length && (char.IsLetterOrDigit(_source[_position]) || _source[_position] == '_'))
        {
            _position++;
        }

        var text = _source[start.._position];
        if (text.Length == 1)
        {
            throw Error("A parameter needs a name, like @city.", start);
        }

        return new CuteToken(CuteTokenKind.Parameter, text, start, CuteValue.String(text[1..]));
    }

    private char Peek() => _position < _source.Length ? _source[_position] : '\0';

    private CuteQueryException Error(string message, int position) => new(message, _source, position);
}
