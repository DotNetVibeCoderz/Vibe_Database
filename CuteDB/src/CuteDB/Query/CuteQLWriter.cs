using System.Globalization;
using System.Text;

namespace CuteDB.Query;

/// <summary>
/// Renders a parsed statement back to CuteQL text.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a LINQ query inspectable. A query provider that silently turns an expression
/// tree into something you cannot see is a provider you cannot debug, so
/// <see cref="CuteQueryableExtensions.ToCuteQL{T}"/> renders the translated statement through here
/// and hands it back as text you can paste into <c>cutedb shell</c>.
/// </para>
/// <para>
/// The output is deliberately re-parseable: running it back through <see cref="CuteParser"/> gives
/// an equivalent statement. That is worth more than pretty formatting, and it is what
/// <c>CuteQLWriterTests</c> checks.
/// </para>
/// </remarks>
public static class CuteQLWriter
{
    /// <summary>Renders a statement as a single line.</summary>
    public static string Write(CuteStatement statement) => Write(statement, indented: false);

    /// <summary>Renders a statement, optionally across several lines.</summary>
    public static string Write(CuteStatement statement, bool indented)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var builder = new StringBuilder(128);
        var separator = indented ? "\n" : " ";

        switch (statement)
        {
            case SelectStatement select:
                WriteSelect(builder, select, separator);
                break;

            case InsertStatement insert:
                builder.Append("INSERT INTO ").Append(insert.Collection).Append(separator).Append("VALUES ");
                builder.AppendJoin(", ", insert.Documents.Select(Write));
                break;

            case UpdateStatement update:
                builder.Append("UPDATE ").Append(update.Collection).Append(separator).Append("SET ");
                builder.AppendJoin(", ", update.Assignments.Select(a => $"{a.Path.Text} = {Write(a.Value)}"));
                if (update.Where is not null)
                {
                    builder.Append(separator).Append("WHERE ").Append(Write(update.Where));
                }

                break;

            case DeleteStatement delete:
                builder.Append("DELETE FROM ").Append(delete.Collection);
                if (delete.Where is not null)
                {
                    builder.Append(separator).Append("WHERE ").Append(Write(delete.Where));
                }

                break;

            default:
                throw new CuteDbException($"Cannot render a {statement.GetType().Name}.");
        }

        return builder.ToString();
    }

    /// <summary>Renders one expression.</summary>
    public static string Write(CuteExpression expression)
    {
        var builder = new StringBuilder(64);
        WriteExpression(builder, expression, parenthesise: false);
        return builder.ToString();
    }

    private static void WriteSelect(StringBuilder builder, SelectStatement select, string separator)
    {
        builder.Append("SELECT ");
        if (select.Distinct)
        {
            builder.Append("DISTINCT ");
        }

        if (select.IsSelectAll)
        {
            builder.Append('*');
        }
        else
        {
            builder.AppendJoin(", ", select.Projections.Select(WriteProjection));
        }

        builder.Append(separator).Append("FROM ").Append(select.Collection);

        if (select.Where is not null)
        {
            builder.Append(separator).Append("WHERE ").Append(Write(select.Where));
        }

        if (select.GroupBy.Count > 0)
        {
            builder.Append(separator).Append("GROUP BY ").AppendJoin(", ", select.GroupBy.Select(Write));
        }

        if (select.Having is not null)
        {
            builder.Append(separator).Append("HAVING ").Append(Write(select.Having));
        }

        if (select.OrderBy.Count > 0)
        {
            builder.Append(separator).Append("ORDER BY ").AppendJoin(
                ", ",
                select.OrderBy.Select(o => Write(o.Expression) + (o.Descending ? " DESC" : string.Empty)));
        }

        if (select.Limit is { } limit)
        {
            builder.Append(separator).Append("LIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));
        }

        if (select.Offset > 0)
        {
            builder.Append(separator).Append("OFFSET ").Append(select.Offset.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string WriteProjection(CuteProjection projection)
    {
        var text = Write(projection.Expression);

        // The alias is omitted when it is what the expression would be called anyway, so a plain
        // projection renders as `code` rather than `code AS code`.
        return projection.Alias is null || projection.Alias == text
            ? text
            : $"{text} AS {projection.Alias}";
    }

    private static void WriteExpression(StringBuilder builder, CuteExpression expression, bool parenthesise)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                WriteLiteral(builder, literal.Value);
                return;

            case ParameterExpression parameter:
                builder.Append('@').Append(parameter.Name);
                return;

            case PathExpression path:
                builder.Append(path.Path.Text);
                return;

            case StarExpression:
                builder.Append('*');
                return;

            case UnaryExpression unary:
                if (unary.Operator == CuteUnaryOperator.Not)
                {
                    builder.Append("NOT ");
                    WriteExpression(builder, unary.Operand, parenthesise: true);
                }
                else
                {
                    builder.Append('-');
                    WriteExpression(builder, unary.Operand, parenthesise: true);
                }

                return;

            case BinaryExpression binary:
            {
                // Parentheses only where precedence would otherwise change the meaning, so the
                // output reads like something a person would write. Restricting this to AND/OR
                // was a bug: it dropped the brackets from (total + 1000) * 2.
                var needed = parenthesise;
                if (needed)
                {
                    builder.Append('(');
                }

                WriteExpression(builder, binary.Left, Precedence(binary.Left) > Precedence(binary));
                builder.Append(' ').Append(binary.Symbol).Append(' ');
                WriteExpression(builder, binary.Right, Precedence(binary.Right) >= Precedence(binary));

                if (needed)
                {
                    builder.Append(')');
                }

                return;
            }

            case BetweenExpression between:
                WriteExpression(builder, between.Value, parenthesise: true);
                builder.Append(between.Negated ? " NOT BETWEEN " : " BETWEEN ");
                WriteExpression(builder, between.Low, parenthesise: true);
                builder.Append(" AND ");
                WriteExpression(builder, between.High, parenthesise: true);
                return;

            case InExpression inExpression:
                WriteExpression(builder, inExpression.Value, parenthesise: true);
                builder.Append(inExpression.Negated ? " NOT IN (" : " IN (");
                builder.AppendJoin(", ", inExpression.Items.Select(Write));
                builder.Append(')');
                return;

            case IsExpression isExpression:
                WriteExpression(builder, isExpression.Value, parenthesise: true);
                builder.Append(" IS ");
                if (isExpression.Negated)
                {
                    builder.Append("NOT ");
                }

                builder.Append(isExpression.Missing ? "MISSING" : "NULL");
                return;

            case FunctionExpression function:
                builder.Append(function.Name).Append('(');
                builder.AppendJoin(", ", function.Arguments.Select(Write));
                builder.Append(')');
                return;

            case ArrayExpression array:
                builder.Append('[');
                builder.AppendJoin(", ", array.Items.Select(Write));
                builder.Append(']');
                return;

            case ObjectExpression obj:
                builder.Append('{');
                builder.AppendJoin(", ", obj.Fields.Select(f => $"'{Escape(f.Key)}': {Write(f.Value)}"));
                builder.Append('}');
                return;

            default:
                throw new CuteDbException($"Cannot render a {expression.GetType().Name}.");
        }
    }

    private static void WriteLiteral(StringBuilder builder, CuteValue value)
    {
        switch (value.Type)
        {
            case CuteType.Null:
                builder.Append("NULL");
                return;

            case CuteType.Missing:
                builder.Append("MISSING");
                return;

            case CuteType.True:
                builder.Append("TRUE");
                return;

            case CuteType.False:
                builder.Append("FALSE");
                return;

            case CuteType.Int32:
            case CuteType.Int64:
                builder.Append(value.AsInt64.ToString(CultureInfo.InvariantCulture));
                return;

            case CuteType.Double:
                builder.Append(value.AsDouble.ToString("R", CultureInfo.InvariantCulture));
                return;

            case CuteType.Decimal:
                builder.Append(value.AsDecimal.ToString(CultureInfo.InvariantCulture));
                return;

            case CuteType.String:
                builder.Append('\'').Append(Escape(value.AsString)).Append('\'');
                return;

            case CuteType.Array:
                builder.Append('[');
                builder.AppendJoin(", ", value.AsArray.AsSpan().ToArray().Select(item =>
                {
                    var inner = new StringBuilder();
                    WriteLiteral(inner, item);
                    return inner.ToString();
                }));

                builder.Append(']');
                return;

            default:
                // Dates, GUIDs and ids have no literal syntax in CuteQL, so they render as the
                // quoted text a comparison against them would use.
                builder.Append('\'').Append(Escape(value.ToDisplayString())).Append('\'');
                return;
        }
    }

    /// <summary>A single-quoted string doubles its quotes, as in SQL.</summary>
    private static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    private static int Precedence(CuteExpression expression) => expression switch
    {
        BinaryExpression { Operator: CuteBinaryOperator.Or } => 5,
        BinaryExpression { Operator: CuteBinaryOperator.And } => 4,
        UnaryExpression { Operator: CuteUnaryOperator.Not } => 3,
        BinaryExpression { Operator: CuteBinaryOperator.Add or CuteBinaryOperator.Subtract } => 1,
        BinaryExpression { Operator: CuteBinaryOperator.Multiply or CuteBinaryOperator.Divide or CuteBinaryOperator.Modulo } => 0,
        BinaryExpression => 2,
        BetweenExpression or InExpression or IsExpression => 2,
        _ => 0,
    };
}
