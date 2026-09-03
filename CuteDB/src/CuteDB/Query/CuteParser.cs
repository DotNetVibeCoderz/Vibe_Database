namespace CuteDB.Query;

/// <summary>
/// Parses CuteQL text into a statement tree.
/// </summary>
/// <remarks>
/// <para>
/// A recursive-descent parser with the usual precedence ladder: <c>OR</c> below <c>AND</c> below
/// <c>NOT</c> below the comparisons below arithmetic. Everything it accepts is listed in
/// <c>docs/en/cuteql.md</c>; everything it rejects, it rejects with the offending character
/// underlined, because a query language people type by hand lives or dies on its error messages.
/// </para>
/// <para>
/// The one place this departs from SQL is that field paths are lexed whole, so
/// <c>customer.address.city</c> is one token rather than three. That removes the ambiguity between
/// a qualified column name and a member access, which a document store cannot otherwise resolve
/// without a schema.
/// </para>
/// </remarks>
public sealed class CuteParser
{
    private readonly string _source;
    private readonly List<CuteToken> _tokens;
    private int _index;

    private CuteParser(string source)
    {
        _source = source;
        _tokens = new CuteLexer(source).Tokenize();
    }

    /// <summary>Parses a complete statement.</summary>
    public static CuteStatement ParseStatement(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var parser = new CuteParser(query);
        var statement = parser.ParseStatementCore();
        parser.ExpectEnd();
        return statement;
    }

    /// <summary>Parses a bare expression, as used by filter-only APIs.</summary>
    public static CuteExpression ParseExpression(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var parser = new CuteParser(expression);
        var parsed = parser.ParseOr();
        parser.ExpectEnd();
        return parsed;
    }

    private CuteStatement ParseStatementCore()
    {
        var token = Current;
        if (token.IsKeyword("SELECT"))
        {
            return ParseSelect();
        }

        if (token.IsKeyword("DELETE"))
        {
            return ParseDelete();
        }

        if (token.IsKeyword("UPDATE"))
        {
            return ParseUpdate();
        }

        if (token.IsKeyword("INSERT"))
        {
            return ParseInsert();
        }

        throw Error("A query starts with SELECT, INSERT, UPDATE or DELETE.", token.Position);
    }

    private SelectStatement ParseSelect()
    {
        ExpectKeyword("SELECT");
        var distinct = TryTakeKeyword("DISTINCT");

        var projections = new List<CuteProjection>();
        if (Current.Kind == CuteTokenKind.Star && PeekAfterStarIsFrom())
        {
            Advance();
        }
        else
        {
            do
            {
                var expression = ParseOr();
                string? alias = null;
                if (TryTakeKeyword("AS"))
                {
                    alias = ExpectIdentifier("an alias");
                }
                else if (Current.Kind == CuteTokenKind.Identifier && !Current.Text.Contains('.'))
                {
                    // SQL lets the AS be implicit: SELECT total t.
                    alias = Current.Text;
                    Advance();
                }

                projections.Add(new CuteProjection(expression, alias));
            }
            while (TryTake(CuteTokenKind.Comma));
        }

        ExpectKeyword("FROM");
        var collection = ExpectIdentifier("a collection name");

        var where = TryTakeKeyword("WHERE") ? ParseOr() : null;

        var groupBy = new List<CuteExpression>();
        if (TryTakeKeyword("GROUP"))
        {
            ExpectKeyword("BY");
            do
            {
                groupBy.Add(ParseOr());
            }
            while (TryTake(CuteTokenKind.Comma));
        }

        var having = TryTakeKeyword("HAVING") ? ParseOr() : null;

        var orderBy = new List<CuteOrdering>();
        if (TryTakeKeyword("ORDER"))
        {
            ExpectKeyword("BY");
            do
            {
                var expression = ParseOr();
                var descending = false;
                if (TryTakeKeyword("DESC"))
                {
                    descending = true;
                }
                else
                {
                    TryTakeKeyword("ASC");
                }

                orderBy.Add(new CuteOrdering(expression, descending));
            }
            while (TryTake(CuteTokenKind.Comma));
        }

        int? limit = null;
        var offset = 0;
        if (TryTakeKeyword("LIMIT"))
        {
            limit = ExpectNonNegativeInteger("LIMIT");
        }

        if (TryTakeKeyword("OFFSET"))
        {
            offset = ExpectNonNegativeInteger("OFFSET");
        }

        return new SelectStatement
        {
            Text = _source,
            Collection = collection,
            Projections = projections,
            Where = where,
            GroupBy = groupBy,
            Having = having,
            OrderBy = orderBy,
            Limit = limit,
            Offset = offset,
            Distinct = distinct,
        };
    }

    private DeleteStatement ParseDelete()
    {
        ExpectKeyword("DELETE");
        ExpectKeyword("FROM");
        var collection = ExpectIdentifier("a collection name");
        var where = TryTakeKeyword("WHERE") ? ParseOr() : null;

        return new DeleteStatement
        {
            Text = _source,
            Collection = collection,
            Where = where,
        };
    }

    private UpdateStatement ParseUpdate()
    {
        ExpectKeyword("UPDATE");
        var collection = ExpectIdentifier("a collection name");
        ExpectKeyword("SET");

        var assignments = new List<CuteAssignment>();
        do
        {
            var target = ExpectIdentifier("a field path");
            Expect(CuteTokenKind.Equal, "'=' after the field being set");
            assignments.Add(new CuteAssignment(CutePath.Parse(target), ParseOr()));
        }
        while (TryTake(CuteTokenKind.Comma));

        var where = TryTakeKeyword("WHERE") ? ParseOr() : null;

        return new UpdateStatement
        {
            Text = _source,
            Collection = collection,
            Assignments = assignments,
            Where = where,
        };
    }

    private InsertStatement ParseInsert()
    {
        ExpectKeyword("INSERT");
        ExpectKeyword("INTO");
        var collection = ExpectIdentifier("a collection name");
        ExpectKeyword("VALUES");

        var documents = new List<CuteExpression>();
        do
        {
            var start = Current.Position;
            var document = ParsePrimary();
            if (document is not ObjectExpression)
            {
                throw Error("INSERT takes object literals, like {'name': 'Budi', 'city': 'Bandung'}.", start);
            }

            documents.Add(document);
        }
        while (TryTake(CuteTokenKind.Comma));

        return new InsertStatement
        {
            Text = _source,
            Collection = collection,
            Documents = documents,
        };
    }

    private CuteExpression ParseOr()
    {
        var left = ParseAnd();
        while (Current.IsKeyword("OR"))
        {
            var position = Current.Position;
            Advance();
            left = new BinaryExpression(CuteBinaryOperator.Or, left, ParseAnd()) { Position = position };
        }

        return left;
    }

    private CuteExpression ParseAnd()
    {
        var left = ParseNot();
        while (Current.IsKeyword("AND"))
        {
            var position = Current.Position;
            Advance();
            left = new BinaryExpression(CuteBinaryOperator.And, left, ParseNot()) { Position = position };
        }

        return left;
    }

    private CuteExpression ParseNot()
    {
        if (Current.IsKeyword("NOT"))
        {
            var position = Current.Position;
            Advance();
            return new UnaryExpression(CuteUnaryOperator.Not, ParseNot()) { Position = position };
        }

        return ParseComparison();
    }

    private CuteExpression ParseComparison()
    {
        var left = ParseAdditive();
        var position = Current.Position;

        switch (Current.Kind)
        {
            case CuteTokenKind.Equal:
                Advance();
                return new BinaryExpression(CuteBinaryOperator.Equal, left, ParseAdditive()) { Position = position };

            case CuteTokenKind.NotEqual:
                Advance();
                return new BinaryExpression(CuteBinaryOperator.NotEqual, left, ParseAdditive()) { Position = position };

            case CuteTokenKind.Less:
                Advance();
                return new BinaryExpression(CuteBinaryOperator.Less, left, ParseAdditive()) { Position = position };

            case CuteTokenKind.LessOrEqual:
                Advance();
                return new BinaryExpression(CuteBinaryOperator.LessOrEqual, left, ParseAdditive()) { Position = position };

            case CuteTokenKind.Greater:
                Advance();
                return new BinaryExpression(CuteBinaryOperator.Greater, left, ParseAdditive()) { Position = position };

            case CuteTokenKind.GreaterOrEqual:
                Advance();
                return new BinaryExpression(CuteBinaryOperator.GreaterOrEqual, left, ParseAdditive()) { Position = position };
        }

        if (Current.IsKeyword("LIKE"))
        {
            Advance();
            return new BinaryExpression(CuteBinaryOperator.Like, left, ParseAdditive()) { Position = position };
        }

        if (Current.IsKeyword("IN"))
        {
            Advance();
            return ParseInTail(left, negated: false, position);
        }

        if (Current.IsKeyword("BETWEEN"))
        {
            Advance();
            return ParseBetweenTail(left, negated: false, position);
        }

        if (Current.IsKeyword("IS"))
        {
            Advance();
            var negated = TryTakeKeyword("NOT");
            if (TryTakeKeyword("MISSING"))
            {
                return new IsExpression(left, negated, missing: true) { Position = position };
            }

            ExpectKeyword("NULL");
            return new IsExpression(left, negated, missing: false) { Position = position };
        }

        // NOT binds tighter than a comparison when it follows one: `x NOT IN (…)`, `x NOT LIKE …`.
        if (Current.IsKeyword("NOT"))
        {
            var notPosition = Current.Position;
            Advance();

            if (Current.IsKeyword("IN"))
            {
                Advance();
                return ParseInTail(left, negated: true, notPosition);
            }

            if (Current.IsKeyword("BETWEEN"))
            {
                Advance();
                return ParseBetweenTail(left, negated: true, notPosition);
            }

            if (Current.IsKeyword("LIKE"))
            {
                Advance();
                return new BinaryExpression(CuteBinaryOperator.NotLike, left, ParseAdditive()) { Position = notPosition };
            }

            throw Error("After NOT here, expected IN, LIKE or BETWEEN.", Current.Position);
        }

        return left;
    }

    private CuteExpression ParseInTail(CuteExpression value, bool negated, int position)
    {
        var items = new List<CuteExpression>();

        // Both spellings are accepted: IN ('a','b') as SQL writes it, and IN ['a','b'] as the
        // array literal it really is.
        if (TryTake(CuteTokenKind.LeftParen))
        {
            if (Current.Kind != CuteTokenKind.RightParen)
            {
                do
                {
                    items.Add(ParseOr());
                }
                while (TryTake(CuteTokenKind.Comma));
            }

            Expect(CuteTokenKind.RightParen, "')' to close the IN list");
        }
        else if (Current.Kind == CuteTokenKind.LeftBracket)
        {
            var array = (ArrayExpression)ParsePrimary();
            items.AddRange(array.Items);
        }
        else
        {
            // IN @parameter, where the parameter holds an array.
            items.Add(ParseAdditive());
        }

        return new InExpression(value, items, negated) { Position = position };
    }

    private CuteExpression ParseBetweenTail(CuteExpression value, bool negated, int position)
    {
        var low = ParseAdditive();
        ExpectKeyword("AND");
        var high = ParseAdditive();
        return new BetweenExpression(value, low, high, negated) { Position = position };
    }

    private CuteExpression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Kind is CuteTokenKind.Plus or CuteTokenKind.Minus)
        {
            var op = Current.Kind == CuteTokenKind.Plus ? CuteBinaryOperator.Add : CuteBinaryOperator.Subtract;
            var position = Current.Position;
            Advance();
            left = new BinaryExpression(op, left, ParseMultiplicative()) { Position = position };
        }

        return left;
    }

    private CuteExpression ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Current.Kind is CuteTokenKind.Star or CuteTokenKind.Slash or CuteTokenKind.Percent)
        {
            var op = Current.Kind switch
            {
                CuteTokenKind.Star => CuteBinaryOperator.Multiply,
                CuteTokenKind.Slash => CuteBinaryOperator.Divide,
                _ => CuteBinaryOperator.Modulo,
            };

            var position = Current.Position;
            Advance();
            left = new BinaryExpression(op, left, ParseUnary()) { Position = position };
        }

        return left;
    }

    private CuteExpression ParseUnary()
    {
        if (Current.Kind == CuteTokenKind.Minus)
        {
            var position = Current.Position;
            Advance();
            return new UnaryExpression(CuteUnaryOperator.Negate, ParseUnary()) { Position = position };
        }

        if (Current.Kind == CuteTokenKind.Plus)
        {
            Advance();
            return ParseUnary();
        }

        return ParsePrimary();
    }

    private CuteExpression ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case CuteTokenKind.Number:
            case CuteTokenKind.String:
                Advance();
                return new LiteralExpression(token.Value) { Position = token.Position };

            case CuteTokenKind.Parameter:
                Advance();
                return new ParameterExpression(token.Value.AsString) { Position = token.Position };

            case CuteTokenKind.Star:
                Advance();
                return new StarExpression { Position = token.Position };

            case CuteTokenKind.LeftParen:
            {
                Advance();
                var inner = ParseOr();
                Expect(CuteTokenKind.RightParen, "')'");
                return inner;
            }

            case CuteTokenKind.LeftBracket:
            {
                Advance();
                var items = new List<CuteExpression>();
                if (Current.Kind != CuteTokenKind.RightBracket)
                {
                    do
                    {
                        items.Add(ParseOr());
                    }
                    while (TryTake(CuteTokenKind.Comma));
                }

                Expect(CuteTokenKind.RightBracket, "']' to close the array");
                return new ArrayExpression(items) { Position = token.Position };
            }

            case CuteTokenKind.LeftBrace:
                return ParseObjectLiteral();

            case CuteTokenKind.Keyword when token.IsKeyword("NULL"):
                Advance();
                return new LiteralExpression(CuteValue.Null) { Position = token.Position };

            case CuteTokenKind.Keyword when token.IsKeyword("TRUE"):
                Advance();
                return new LiteralExpression(CuteValue.Boolean(true)) { Position = token.Position };

            case CuteTokenKind.Keyword when token.IsKeyword("FALSE"):
                Advance();
                return new LiteralExpression(CuteValue.Boolean(false)) { Position = token.Position };

            case CuteTokenKind.Keyword when token.IsKeyword("MISSING"):
                Advance();
                return new LiteralExpression(CuteValue.Missing) { Position = token.Position };

            // COUNT, SUM, AVG, MIN and MAX are keywords so they cannot be used as bare field
            // names, but in expression position they are function calls like any other.
            case CuteTokenKind.Keyword when CuteFunctions.IsAggregate(token.Text.ToUpperInvariant()):
            case CuteTokenKind.Identifier when Peek(1).Kind == CuteTokenKind.LeftParen:
                return ParseFunctionCall();

            case CuteTokenKind.Identifier:
                Advance();
                if (!CutePath.TryParse(token.Text, out var path, out var pathError))
                {
                    throw Error($"'{token.Text}' is not a usable field path: {pathError}", token.Position);
                }

                return new PathExpression(path) { Position = token.Position };

            default:
                throw Error($"Expected a value here, found {token}.", token.Position);
        }
    }

    private CuteExpression ParseFunctionCall()
    {
        var token = Current;
        var name = token.Text;
        Advance();
        Expect(CuteTokenKind.LeftParen, $"'(' after {name}");

        var arguments = new List<CuteExpression>();
        if (Current.Kind != CuteTokenKind.RightParen)
        {
            do
            {
                arguments.Add(ParseOr());
            }
            while (TryTake(CuteTokenKind.Comma));
        }

        Expect(CuteTokenKind.RightParen, $"')' to close {name}(");

        if (!CuteFunctions.IsKnown(name))
        {
            throw Error(
                $"There is no function called {name.ToUpperInvariant()}. Available: {CuteFunctions.NamesForHelp}.",
                token.Position);
        }

        return new FunctionExpression(name, arguments) { Position = token.Position };
    }

    private CuteExpression ParseObjectLiteral()
    {
        var start = Current.Position;
        Expect(CuteTokenKind.LeftBrace, "'{'");

        var fields = new List<KeyValuePair<string, CuteExpression>>();
        if (Current.Kind != CuteTokenKind.RightBrace)
        {
            do
            {
                var key = Current.Kind switch
                {
                    CuteTokenKind.String => Current.Value.AsString,
                    CuteTokenKind.Identifier or CuteTokenKind.Keyword => Current.Text,
                    _ => throw Error("An object key has to be a string or a bare name.", Current.Position),
                };

                Advance();
                Expect(CuteTokenKind.Colon, "':' after the key");
                fields.Add(new KeyValuePair<string, CuteExpression>(key, ParseOr()));
            }
            while (TryTake(CuteTokenKind.Comma));
        }

        Expect(CuteTokenKind.RightBrace, "'}' to close the object");
        return new ObjectExpression(fields) { Position = start };
    }

    private bool PeekAfterStarIsFrom() => Peek(1).IsKeyword("FROM");

    private CuteToken Current => _tokens[_index];

    private CuteToken Peek(int offset)
        => _index + offset < _tokens.Count ? _tokens[_index + offset] : _tokens[^1];

    private void Advance()
    {
        if (_index < _tokens.Count - 1)
        {
            _index++;
        }
    }

    private bool TryTake(CuteTokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }

    private bool TryTakeKeyword(string keyword)
    {
        if (!Current.IsKeyword(keyword))
        {
            return false;
        }

        Advance();
        return true;
    }

    private void Expect(CuteTokenKind kind, string what)
    {
        if (Current.Kind != kind)
        {
            throw Error($"Expected {what}, found {Current}.", Current.Position);
        }

        Advance();
    }

    private void ExpectKeyword(string keyword)
    {
        if (!Current.IsKeyword(keyword))
        {
            throw Error($"Expected {keyword}, found {Current}.", Current.Position);
        }

        Advance();
    }

    private string ExpectIdentifier(string what)
    {
        if (Current.Kind != CuteTokenKind.Identifier)
        {
            throw Error($"Expected {what}, found {Current}.", Current.Position);
        }

        var text = Current.Text;
        Advance();
        return text;
    }

    private int ExpectNonNegativeInteger(string clause)
    {
        if (Current.Kind != CuteTokenKind.Number || !Current.Value.IsNumber)
        {
            throw Error($"{clause} needs a number, found {Current}.", Current.Position);
        }

        var value = Current.Value.AsInt64;
        if (value < 0 || value > int.MaxValue)
        {
            throw Error($"{clause} has to be between 0 and {int.MaxValue}.", Current.Position);
        }

        Advance();
        return (int)value;
    }

    private void ExpectEnd()
    {
        if (Current.Kind != CuteTokenKind.End)
        {
            throw Error($"Expected the query to end, found {Current}.", Current.Position);
        }
    }

    private CuteQueryException Error(string message, int position) => new(message, _source, position);
}
