using System.Text;

namespace MemSharp.Query;

/// <summary>
/// Parses the MemSharp query dialect into a <see cref="SqlQuery"/>.
/// </summary>
/// <remarks>
/// <para>
/// A recursive-descent parser over <see cref="SqlTokenizer"/>. The grammar it accepts:
/// </para>
/// <code>
/// SELECT (* | column [, column]...) FROM KEYS
///   [WHERE condition]
///   [ORDER BY column [ASC | DESC]]
///   [LIMIT n [OFFSET m]]
///
/// DELETE FROM KEYS [WHERE condition]
///
/// condition := term [(AND | OR) term]...
/// term      := NOT term | '(' condition ')' | comparison
/// comparison:= column (= | != | &lt; | &lt;= | &gt; | &gt;= | LIKE | NOT LIKE) literal
///            | column IN '(' literal [, literal]... ')'
/// column    := key | type | size | ttl | value
/// </code>
/// <para>
/// One table, <c>KEYS</c>, whose rows are the keyspace: one row per key, with the key's name, type,
/// size, remaining TTL and - for strings - its value. Joins, projections over collection elements
/// and aggregates are deliberately absent; this is a keyspace browser, not a relational engine, and
/// pretending otherwise would be the more misleading design.
/// </para>
/// </remarks>
public static class SqlParser
{
    /// <summary>Parses a query. Throws <see cref="MemSharpCommandException"/> on a syntax error.</summary>
    public static SqlQuery Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var tokenizer = new SqlTokenizer(sql);
        var parser = new Parser(tokenizer.Tokenize());
        return parser.ParseStatement();
    }

    /// <summary>Parses a query, returning false instead of throwing.</summary>
    public static bool TryParse(string sql, out SqlQuery? query, out string? error)
    {
        try
        {
            query = Parse(sql);
            error = null;
            return true;
        }
        catch (MemSharpException ex)
        {
            query = null;
            error = ex.Message;
            return false;
        }
    }

    private sealed class Parser(List<Token> tokens)
    {
        private readonly List<Token> _tokens = tokens;
        private int _index;

        private Token Current => _tokens[_index];

        public SqlQuery ParseStatement()
        {
            bool isDelete;
            var columns = new List<QueryColumn>();

            if (Current.Is("SELECT"))
            {
                isDelete = false;
                _index++;
                ParseProjection(columns);
            }
            else if (Current.Is("DELETE"))
            {
                isDelete = true;
                _index++;
            }
            else
            {
                throw Error($"expected SELECT or DELETE, found {Current}");
            }

            Expect("FROM");
            if (!Current.Is("KEYS"))
            {
                throw Error($"the only table is KEYS, found {Current}");
            }
            _index++;

            QueryPredicate? where = null;
            if (Current.Is("WHERE"))
            {
                _index++;
                where = ParseCondition();
            }

            QueryColumn? orderBy = null;
            bool descending = false;
            if (Current.Is("ORDER"))
            {
                _index++;
                Expect("BY");
                orderBy = ParseColumn();
                if (Current.Is("DESC")) { descending = true; _index++; }
                else if (Current.Is("ASC")) _index++;
            }

            int limit = -1, offset = 0;
            if (Current.Is("LIMIT"))
            {
                _index++;
                limit = ParseInteger();
                if (Current.Is("OFFSET"))
                {
                    _index++;
                    offset = ParseInteger();
                }
            }

            if (Current.Kind != TokenKind.End)
            {
                throw Error($"unexpected {Current} after the end of the statement");
            }

            if (isDelete && (orderBy is not null || limit >= 0))
            {
                throw Error("DELETE does not accept ORDER BY or LIMIT");
            }

            return new SqlQuery
            {
                IsDelete = isDelete,
                Columns = columns,
                Where = where,
                OrderBy = orderBy,
                Descending = descending,
                Limit = limit,
                Offset = offset,
                KeyPattern = ExtractKeyPattern(where),
            };
        }

        private void ParseProjection(List<QueryColumn> columns)
        {
            if (Current.Kind == TokenKind.Star)
            {
                _index++;
                return;   // empty list means every column
            }

            while (true)
            {
                columns.Add(ParseColumn());
                if (Current.Kind != TokenKind.Comma) break;
                _index++;
            }
        }

        private QueryPredicate ParseCondition()
        {
            var left = ParseTerm();
            while (Current.Is("AND") || Current.Is("OR"))
            {
                bool isAnd = Current.Is("AND");
                _index++;
                var right = ParseTerm();
                left = isAnd ? new AndPredicate(left, right) : new OrPredicate(left, right);
            }
            return left;
        }

        private QueryPredicate ParseTerm()
        {
            if (Current.Is("NOT"))
            {
                _index++;
                return new NotPredicate(ParseTerm());
            }

            if (Current.Kind == TokenKind.OpenParen)
            {
                _index++;
                var inner = ParseCondition();
                if (Current.Kind != TokenKind.CloseParen) throw Error($"expected ')', found {Current}");
                _index++;
                return inner;
            }

            var column = ParseColumn();

            if (Current.Is("IN"))
            {
                _index++;
                if (Current.Kind != TokenKind.OpenParen) throw Error($"expected '(' after IN, found {Current}");
                _index++;

                var values = new List<string>();
                while (Current.Kind != TokenKind.CloseParen)
                {
                    values.Add(ParseLiteral());
                    if (Current.Kind == TokenKind.Comma) _index++;
                    else break;
                }
                if (Current.Kind != TokenKind.CloseParen) throw Error($"expected ')' to close IN, found {Current}");
                _index++;

                if (values.Count == 0) throw Error("IN needs at least one value");
                return new ComparisonPredicate(column, ComparisonOperator.In, values.ToArray());
            }

            if (Current.Is("NOT"))
            {
                _index++;
                if (!Current.Is("LIKE")) throw Error($"expected LIKE after NOT, found {Current}");
                _index++;
                return new NotPredicate(new ComparisonPredicate(column, ComparisonOperator.Like, [ParseLiteral()]));
            }

            if (Current.Is("LIKE"))
            {
                _index++;
                return new ComparisonPredicate(column, ComparisonOperator.Like, [ParseLiteral()]);
            }

            if (Current.Kind != TokenKind.Operator) throw Error($"expected a comparison operator, found {Current}");
            var op = Current.Text switch
            {
                "=" => ComparisonOperator.Equal,
                "!=" => ComparisonOperator.NotEqual,
                "<" => ComparisonOperator.LessThan,
                "<=" => ComparisonOperator.LessThanOrEqual,
                ">" => ComparisonOperator.GreaterThan,
                ">=" => ComparisonOperator.GreaterThanOrEqual,
                _ => throw Error($"unknown operator '{Current.Text}'"),
            };
            _index++;

            return new ComparisonPredicate(column, op, [ParseLiteral()]);
        }

        private QueryColumn ParseColumn()
        {
            if (Current.Kind != TokenKind.Identifier) throw Error($"expected a column name, found {Current}");

            var column = Current.Text.ToUpperInvariant() switch
            {
                "KEY" => QueryColumn.Key,
                "TYPE" => QueryColumn.Type,
                "SIZE" or "LEN" or "LENGTH" => QueryColumn.Size,
                "TTL" => QueryColumn.Ttl,
                "VALUE" or "VAL" => QueryColumn.Value,
                _ => throw Error($"unknown column '{Current.Text}'; the columns are key, type, size, ttl and value"),
            };
            _index++;
            return column;
        }

        private string ParseLiteral()
        {
            if (Current.Kind is TokenKind.String or TokenKind.Number or TokenKind.Identifier)
            {
                string text = Current.Text;
                _index++;
                return text;
            }
            throw Error($"expected a literal, found {Current}");
        }

        private int ParseInteger()
        {
            if (Current.Kind != TokenKind.Number || !int.TryParse(Current.Text, out int value))
            {
                throw Error($"expected a whole number, found {Current}");
            }
            _index++;
            return value;
        }

        private void Expect(string keyword)
        {
            if (!Current.Is(keyword)) throw Error($"expected {keyword}, found {Current}");
            _index++;
        }

        private static MemSharpCommandException Error(string message) =>
            new($"syntax error: {message}");

        /// <summary>
        /// Finds a key pattern the executor can push down into <see cref="MemDb.Scan"/>.
        /// </summary>
        /// <remarks>
        /// Only a top-level <c>key LIKE '...'</c> or <c>key = '...'</c> qualifies, and only when it
        /// is reachable through <c>AND</c> alone. Under an <c>OR</c> a row rejected by this branch
        /// may still be accepted by the other, so narrowing the scan would silently drop rows -
        /// which is why the walk stops at the first <see cref="OrPredicate"/> rather than descending
        /// into it.
        /// </remarks>
        private static string? ExtractKeyPattern(QueryPredicate? predicate) => predicate switch
        {
            ComparisonPredicate { Column: QueryColumn.Key, Operator: ComparisonOperator.Like } c
                => Collections.GlobMatcher.FromSqlLike(c.Operands[0]),
            ComparisonPredicate { Column: QueryColumn.Key, Operator: ComparisonOperator.Equal } c
                => EscapeGlob(c.Operands[0]),
            AndPredicate and => ExtractKeyPattern(and.Left) ?? ExtractKeyPattern(and.Right),
            _ => null,
        };

        private static string EscapeGlob(string literal)
        {
            var builder = new StringBuilder(literal.Length);
            foreach (char c in literal)
            {
                if (c is '*' or '?' or '[' or '\\') builder.Append('\\');
                builder.Append(c);
            }
            return builder.ToString();
        }
    }
}
