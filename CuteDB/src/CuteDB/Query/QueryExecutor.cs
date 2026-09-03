namespace CuteDB.Query;

/// <summary>
/// Runs a parsed CuteQL statement against a database.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is the familiar one — find rows, group, aggregate, filter the groups, project,
/// deduplicate, sort, page — with one structural choice worth calling out: sort keys are computed
/// once per row into an array and the sort runs over row indices, rather than the comparer
/// re-evaluating expressions on every comparison. An <c>ORDER BY</c> over a computed expression
/// otherwise evaluates it O(n log n) times instead of n.
/// </para>
/// <para>
/// Aliases are resolvable from <c>ORDER BY</c>, so <c>SELECT COUNT(*) AS orders … ORDER BY orders
/// DESC</c> works. SQL disagrees about whether that should be legal; it is what people expect, and
/// the ambiguity it creates (an alias shadowing a real field) resolves in favour of the alias only
/// when the name is not also a field on the row.
/// </para>
/// </remarks>
internal static class QueryExecutor
{
    internal static CuteQueryResult Execute(CuteDatabase database, CuteStatement statement, CuteParameters? parameters)
    {
        return statement switch
        {
            SelectStatement select => ExecuteSelect(database, select, parameters),
            InsertStatement insert => ExecuteInsert(database, insert, parameters),
            UpdateStatement update => ExecuteUpdate(database, update, parameters),
            DeleteStatement delete => ExecuteDelete(database, delete, parameters),
            _ => throw new CuteDbException($"Cannot execute a {statement.GetType().Name}."),
        };
    }

    private static CuteQueryResult ExecuteSelect(CuteDatabase database, SelectStatement select, CuteParameters? parameters)
    {
        var timer = QueryTimer.Start();
        var collection = database.RequireCollection(select.Collection);

        return database.Read(collection, (select, parameters, timer), static (c, state) =>
        {
            var (select, parameters, timer) = state;

            List<int> rows;
            CuteQueryPlan plan;
            if (select.Where is null)
            {
                rows = c.FindRows(null, parameters, int.MaxValue);
                plan = new CuteQueryPlan("Full collection", null, rows.Count, rows.Count, false);
            }
            else
            {
                rows = QueryPlanner.Execute(c, select.Where, parameters, int.MaxValue, out plan);
            }

            var grouped = select.GroupBy.Count > 0 || select.HasAggregates || ContainsAggregate(select.Having);
            var projected = grouped
                ? ProjectGrouped(c, select, parameters, rows)
                : ProjectRows(c, select, parameters, rows);

            if (select.Distinct)
            {
                projected = Deduplicate(projected);
            }

            if (select.OrderBy.Count > 0)
            {
                projected = Sort(c, select, parameters, projected, grouped ? null : rows);
            }

            var paged = Page(projected, select.Offset, select.Limit);
            var columns = select.IsSelectAll
                ? CuteQueryResult.DeriveColumns(paged)
                : select.Projections.Select(static p => p.OutputName).ToList();

            // The plan keeps the access method's own numbers: how many rows it examined and how
            // many passed the WHERE clause. Overwriting MatchedRows with the paged count would
            // make `LIMIT 8` over 47,000 matches read as "8 matched", which is the opposite of
            // what someone reading a plan wants to know. The returned row count is reported
            // separately by the caller.
            return new CuteQueryResult(
                CuteQueryKind.Select,
                columns,
                paged,
                paged.Count,
                timer.Elapsed,
                plan);
        });
    }

    private static List<CuteObject> ProjectRows(
        CuteCollection collection,
        SelectStatement select,
        CuteParameters? parameters,
        List<int> rows)
    {
        var store = collection.Store;
        var output = new List<CuteObject>(rows.Count);

        foreach (var row in rows)
        {
            var context = new CuteEvalContext(store.Read(row), parameters);

            if (select.IsSelectAll)
            {
                output.Add(CuteBinary.Decode(store.Read(row)).AsObject);
                continue;
            }

            var projected = new CuteObject(select.Projections.Count);
            foreach (var projection in select.Projections)
            {
                // `SELECT *, computed` is spelled by listing * among the projections; expanding it
                // here keeps that case working without a separate code path.
                if (projection.Expression is StarExpression)
                {
                    foreach (var (key, field) in CuteBinary.Decode(store.Read(row)).AsObject)
                    {
                        projected.Set(key, field);
                    }

                    continue;
                }

                var value = CuteEvaluator.Evaluate(projection.Expression, in context);
                projected.Set(projection.OutputName, value.IsMissing ? CuteValue.Null : value);
            }

            output.Add(projected);
        }

        return output;
    }

    private static List<CuteObject> ProjectGrouped(
        CuteCollection collection,
        SelectStatement select,
        CuteParameters? parameters,
        List<int> rows)
    {
        var store = collection.Store;
        var aggregates = new List<FunctionExpression>();
        foreach (var projection in select.Projections)
        {
            CollectAggregates(projection.Expression, aggregates);
        }

        CollectAggregates(select.Having, aggregates);

        var groups = new Dictionary<GroupKey, GroupState>();
        var order = new List<GroupKey>();

        foreach (var row in rows)
        {
            var context = new CuteEvalContext(store.Read(row), parameters);

            var keyValues = new CuteValue[select.GroupBy.Count];
            for (var i = 0; i < select.GroupBy.Count; i++)
            {
                keyValues[i] = CuteEvaluator.Evaluate(select.GroupBy[i], in context);
            }

            var key = new GroupKey(keyValues);
            if (!groups.TryGetValue(key, out var state))
            {
                state = new GroupState(keyValues, aggregates.Count);
                groups[key] = state;
                order.Add(key);
            }

            for (var i = 0; i < aggregates.Count; i++)
            {
                state.Accumulators[i].Accumulate(aggregates[i], in context);
            }
        }

        // With no GROUP BY and no rows at all, an aggregate query still produces exactly one row —
        // COUNT(*) over an empty collection is 0, not "no answer".
        if (groups.Count == 0 && select.GroupBy.Count == 0)
        {
            var state = new GroupState([], aggregates.Count);
            var key = new GroupKey([]);
            groups[key] = state;
            order.Add(key);
        }

        var output = new List<CuteObject>(groups.Count);
        foreach (var key in order)
        {
            var state = groups[key];

            var resolved = new Dictionary<FunctionExpression, CuteValue>(aggregates.Count);
            for (var i = 0; i < aggregates.Count; i++)
            {
                resolved[aggregates[i]] = state.Accumulators[i].Result(aggregates[i]);
            }

            // The group's own row holds the group keys under their source text, so `SELECT *` on a
            // grouped query shows what it was grouped by. Aggregate values and the key lookup are
            // supplied through the context rather than written into the row, so neither can
            // collide with a real field name.
            var groupRow = new CuteObject(select.GroupBy.Count);
            var keyLookup = new Dictionary<string, CuteValue>(select.GroupBy.Count, StringComparer.Ordinal);
            for (var i = 0; i < select.GroupBy.Count; i++)
            {
                var name = DeriveGroupName(select.GroupBy[i]);
                groupRow.Set(name, state.Keys[i]);
                keyLookup[name] = state.Keys[i];
            }

            var context = new CuteEvalContext(groupRow, parameters)
                .WithAggregates(resolved)
                .WithGroupKeys(keyLookup);

            if (select.Having is not null && !CuteEvaluator.Test(select.Having, in context))
            {
                continue;
            }

            if (select.IsSelectAll)
            {
                output.Add(groupRow);
                continue;
            }

            var projected = new CuteObject(select.Projections.Count);
            foreach (var projection in select.Projections)
            {
                var value = CuteEvaluator.Evaluate(projection.Expression, in context);
                projected.Set(projection.OutputName, value.IsMissing ? CuteValue.Null : value);
            }

            output.Add(projected);
        }

        return output;
    }

    private static List<CuteObject> Deduplicate(List<CuteObject> rows)
    {
        var seen = new HashSet<CuteValue>(CuteValueEqualityComparer.Instance);
        var output = new List<CuteObject>(rows.Count);

        foreach (var row in rows)
        {
            if (seen.Add(CuteValue.Object(row)))
            {
                output.Add(row);
            }
        }

        return output;
    }

    private static List<CuteObject> Sort(
        CuteCollection collection,
        SelectStatement select,
        CuteParameters? parameters,
        List<CuteObject> projected,
        List<int>? sourceRows)
    {
        var termCount = select.OrderBy.Count;
        var keys = new CuteValue[projected.Count * termCount];
        var aliases = select.Projections.Select(static p => p.OutputName).ToHashSet(StringComparer.Ordinal);
        var store = collection.Store;

        for (var i = 0; i < projected.Count; i++)
        {
            for (var t = 0; t < termCount; t++)
            {
                var expression = select.OrderBy[t].Expression;

                // An ORDER BY naming a projection alias sorts on the projected value. Only when
                // the name is not an alias does it fall through to the underlying document.
                if (expression is PathExpression path && aliases.Contains(path.Path.Text)
                    && projected[i].TryGetValue(path.Path.Text, out var aliased))
                {
                    keys[(i * termCount) + t] = aliased;
                    continue;
                }

                if (sourceRows is not null)
                {
                    var sourceContext = new CuteEvalContext(store.Read(sourceRows[i]), parameters);
                    keys[(i * termCount) + t] = CuteEvaluator.Evaluate(expression, in sourceContext);
                }
                else
                {
                    var groupContext = new CuteEvalContext(projected[i], parameters);
                    keys[(i * termCount) + t] = CuteEvaluator.Evaluate(expression, in groupContext);
                }
            }
        }

        var indices = new int[projected.Count];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        var descending = select.OrderBy.Select(static o => o.Descending).ToArray();
        Array.Sort(indices, (left, right) =>
        {
            for (var t = 0; t < termCount; t++)
            {
                var order = CuteValueComparer.Compare(keys[(left * termCount) + t], keys[(right * termCount) + t]);
                if (order != 0)
                {
                    return descending[t] ? -order : order;
                }
            }

            // Array.Sort is not stable, so ties fall back to the original position to keep results
            // reproducible between runs.
            return left.CompareTo(right);
        });

        var sorted = new List<CuteObject>(projected.Count);
        foreach (var index in indices)
        {
            sorted.Add(projected[index]);
        }

        return sorted;
    }

    private static List<CuteObject> Page(List<CuteObject> rows, int offset, int? limit)
    {
        if (offset <= 0 && limit is null)
        {
            return rows;
        }

        var start = Math.Min(offset, rows.Count);
        var count = limit is null ? rows.Count - start : Math.Min(limit.Value, rows.Count - start);
        return rows.GetRange(start, Math.Max(0, count));
    }

    private static CuteQueryResult ExecuteInsert(CuteDatabase database, InsertStatement insert, CuteParameters? parameters)
    {
        var timer = QueryTimer.Start();
        var collection = database.Collection(insert.Collection);

        var documents = new List<CuteDocument>(insert.Documents.Count);
        var empty = new CuteObject();
        foreach (var expression in insert.Documents)
        {
            var context = new CuteEvalContext(empty, parameters);
            var value = CuteEvaluator.Evaluate(expression, in context);
            documents.Add(new CuteDocument(value.AsObject));
        }

        var inserted = collection.InsertMany(documents);
        return CuteQueryResult.ForWrite(CuteQueryKind.Insert, inserted, timer.Elapsed);
    }

    private static CuteQueryResult ExecuteUpdate(CuteDatabase database, UpdateStatement update, CuteParameters? parameters)
    {
        var timer = QueryTimer.Start();
        var collection = database.RequireCollection(update.Collection);

        var affected = database.Write(collection, (update, parameters), static (c, state) =>
        {
            var (update, parameters) = state;
            var rows = c.FindRows(update.Where, parameters, int.MaxValue);

            // The documents are decoded up front because applying an assignment rewrites the row,
            // which invalidates the encoded spans the loop would otherwise still be reading.
            var pending = new List<CuteDocument>(rows.Count);
            foreach (var row in rows)
            {
                pending.Add(CuteBinary.DecodeDocument(c.Store.Read(row)));
            }

            foreach (var document in pending)
            {
                var context = new CuteEvalContext(document.Root, parameters);
                foreach (var assignment in update.Assignments)
                {
                    var value = CuteEvaluator.Evaluate(assignment.Value, in context);
                    assignment.Path.Assign(document.Root, value.IsMissing ? CuteValue.Null : value);
                }

                c.UpsertCore(document, requireNew: false);
            }

            return pending.Count;
        });

        return CuteQueryResult.ForWrite(CuteQueryKind.Update, affected, timer.Elapsed);
    }

    private static CuteQueryResult ExecuteDelete(CuteDatabase database, DeleteStatement delete, CuteParameters? parameters)
    {
        var timer = QueryTimer.Start();
        var collection = database.RequireCollection(delete.Collection);

        var affected = database.Write(collection, (delete, parameters), static (c, state) =>
        {
            var (delete, parameters) = state;
            var rows = c.FindRows(delete.Where, parameters, int.MaxValue);

            var ids = new List<CuteId>(rows.Count);
            foreach (var row in rows)
            {
                ids.Add(c.Store.IdAt(row));
            }

            var removed = 0;
            foreach (var id in ids)
            {
                if (c.DeleteCore(id))
                {
                    removed++;
                }
            }

            return removed;
        });

        return CuteQueryResult.ForWrite(CuteQueryKind.Delete, affected, timer.Elapsed);
    }

    private static string DeriveGroupName(CuteExpression expression) => expression switch
    {
        PathExpression path => path.Path.Text,
        _ => expression.ToString(),
    };

    private static bool ContainsAggregate(CuteExpression? expression)
        => expression is not null && SelectStatement.ContainsAggregate(expression);

    private static void CollectAggregates(CuteExpression? expression, List<FunctionExpression> into)
    {
        switch (expression)
        {
            case null:
                return;

            case FunctionExpression function when function.IsAggregate:
                if (!into.Contains(function))
                {
                    into.Add(function);
                }

                return;

            case FunctionExpression function:
                foreach (var argument in function.Arguments)
                {
                    CollectAggregates(argument, into);
                }

                return;

            case BinaryExpression binary:
                CollectAggregates(binary.Left, into);
                CollectAggregates(binary.Right, into);
                return;

            case UnaryExpression unary:
                CollectAggregates(unary.Operand, into);
                return;

            case BetweenExpression between:
                CollectAggregates(between.Value, into);
                CollectAggregates(between.Low, into);
                CollectAggregates(between.High, into);
                return;

            case InExpression inExpression:
                CollectAggregates(inExpression.Value, into);
                foreach (var item in inExpression.Items)
                {
                    CollectAggregates(item, into);
                }

                return;

            case IsExpression isExpression:
                CollectAggregates(isExpression.Value, into);
                return;
        }
    }

    /// <summary>The values a group is keyed by, compared with CuteDB's own equality.</summary>
    private readonly struct GroupKey(CuteValue[] values) : IEquatable<GroupKey>
    {
        private readonly CuteValue[] _values = values;

        public bool Equals(GroupKey other)
        {
            if (_values.Length != other._values.Length)
            {
                return false;
            }

            for (var i = 0; i < _values.Length; i++)
            {
                if (!CuteValueComparer.Equal(_values[i], other._values[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is GroupKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in _values)
            {
                hash.Add(CuteValueComparer.GetHashCode(value));
            }

            return hash.ToHashCode();
        }
    }

    private sealed class GroupState
    {
        internal GroupState(CuteValue[] keys, int aggregateCount)
        {
            Keys = keys;
            Accumulators = new Accumulator[aggregateCount];
            for (var i = 0; i < aggregateCount; i++)
            {
                Accumulators[i] = new Accumulator();
            }
        }

        internal CuteValue[] Keys { get; }

        internal Accumulator[] Accumulators { get; }
    }

    /// <summary>Running state for one aggregate over one group.</summary>
    private sealed class Accumulator
    {
        private long _count;
        private decimal _sumExact;
        private double _sumApprox;
        private bool _useExact = true;
        private CuteValue _min = CuteValue.Missing;
        private CuteValue _max = CuteValue.Missing;

        internal void Accumulate(FunctionExpression function, scoped in CuteEvalContext context)
        {
            // COUNT(*) counts rows; every other aggregate ignores rows where its argument is
            // absent or null, which is what makes AVG over a sparse field mean what people expect.
            if (function.Arguments.Count == 0 || function.Arguments[0] is StarExpression)
            {
                _count++;
                return;
            }

            var value = CuteEvaluator.Evaluate(function.Arguments[0], in context);
            if (value.IsNullOrMissing)
            {
                return;
            }

            _count++;

            if (value.IsNumber)
            {
                if (_useExact && value.Type != CuteType.Double)
                {
                    var addend = value.Type == CuteType.Decimal ? value.AsDecimal : value.AsInt64;
                    try
                    {
                        _sumExact += addend;
                    }
                    catch (OverflowException)
                    {
                        // A decimal running total that overflows falls back to doubles for the
                        // rest of the group rather than failing the query.
                        _useExact = false;
                        _sumApprox = (double)_sumExact + value.AsDouble;
                    }
                }
                else
                {
                    if (_useExact)
                    {
                        _sumApprox = (double)_sumExact;
                        _useExact = false;
                    }

                    _sumApprox += value.AsDouble;
                }
            }

            if (_min.IsMissing || CuteValueComparer.Compare(value, _min) < 0)
            {
                _min = value;
            }

            if (_max.IsMissing || CuteValueComparer.Compare(value, _max) > 0)
            {
                _max = value;
            }
        }

        internal CuteValue Result(FunctionExpression function) => function.Name switch
        {
            "COUNT" => CuteValue.Int64(_count),
            "SUM" => _count == 0 ? CuteValue.Int32(0) : Sum(),
            "AVG" => _count == 0 ? CuteValue.Null : Average(),
            "MIN" => _min.IsMissing ? CuteValue.Null : _min,
            "MAX" => _max.IsMissing ? CuteValue.Null : _max,
            _ => CuteValue.Null,
        };

        private CuteValue Sum() => _useExact ? CuteValue.Decimal(_sumExact) : CuteValue.Double(_sumApprox);

        private CuteValue Average()
            => _useExact
                ? CuteValue.Decimal(_sumExact / _count)
                : CuteValue.Double(_sumApprox / _count);
    }
}
