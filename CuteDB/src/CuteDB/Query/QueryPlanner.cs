using CuteDB.Indexing;
using CuteDB.Native;

namespace CuteDB.Query;

/// <summary>How a query decided to find its rows, for <c>EXPLAIN</c> and the CLI's timing panel.</summary>
/// <param name="Strategy">The access method chosen.</param>
/// <param name="IndexName">The index used, when one was.</param>
/// <param name="CandidateRows">Rows the access method produced before the full predicate ran.</param>
/// <param name="MatchedRows">Rows that survived the predicate.</param>
/// <param name="UsedNativeScanner">Whether the Rust accelerator ran the scan.</param>
public readonly record struct CuteQueryPlan(
    string Strategy,
    string? IndexName,
    int CandidateRows,
    int MatchedRows,
    bool UsedNativeScanner)
{
    /// <summary>A one-line description for a log or a status bar.</summary>
    public override string ToString() => IndexName is null
        ? $"{Strategy}: {CandidateRows} scanned, {MatchedRows} matched{(UsedNativeScanner ? " (native)" : string.Empty)}"
        : $"{Strategy} on '{IndexName}': {CandidateRows} candidates, {MatchedRows} matched";
}

/// <summary>
/// Chooses how to satisfy a filter — through an index or by scanning — and then runs it.
/// </summary>
/// <remarks>
/// <para>
/// The planner is deliberately simple, because for a store where everything is already in memory
/// the interesting decision is only ever "can an index skip most of the rows?". It breaks the
/// predicate into its top-level <c>AND</c> terms, looks for a term of the form
/// <c>indexed.path OP constant</c>, and picks the one likely to produce the fewest candidates:
/// equality on a unique index first, then plain equality, then a range. Everything the index
/// produces is still re-checked against the whole predicate, so a wrong guess costs time and never
/// correctness.
/// </para>
/// <para>
/// With no usable index it scans, and the scan itself has two implementations — the Rust
/// accelerator when the predicate compiles to bytecode and the library loaded, the managed
/// evaluator otherwise. Callers cannot tell which ran except by the timing and by
/// <see cref="CuteQueryPlan.UsedNativeScanner"/>.
/// </para>
/// </remarks>
public static class QueryPlanner
{
    /// <summary>Finds the rows in a collection matching a predicate.</summary>
    public static List<int> Execute(CuteCollection collection, CuteExpression predicate, CuteParameters? parameters, int limit)
        => Execute(collection, predicate, parameters, limit, out _);

    /// <summary>Finds the matching rows and reports how it did so.</summary>
    public static List<int> Execute(
        CuteCollection collection,
        CuteExpression predicate,
        CuteParameters? parameters,
        int limit,
        out CuteQueryPlan plan)
    {
        var store = collection.Store;

        if (TryPlanIndexLookup(collection, predicate, parameters, out var candidates, out var indexName))
        {
            var matched = new List<int>(Math.Min(candidates.Count, 64));
            foreach (var row in candidates)
            {
                if (!store.IsLive(row))
                {
                    continue;
                }

                var context = new CuteEvalContext(store.Read(row), parameters);
                if (CuteEvaluator.Test(predicate, in context))
                {
                    matched.Add(row);
                    if (matched.Count >= limit)
                    {
                        break;
                    }
                }
            }

            plan = new CuteQueryPlan("Index seek", indexName, candidates.Count, matched.Count, false);
            return matched;
        }

        var results = new List<int>(Math.Min(store.Count, 256));
        var usedNative = NativeScanner.TryScan(store, predicate, parameters, limit, results);
        if (!usedNative)
        {
            ScanManaged(collection, predicate, parameters, limit, results);
        }

        plan = new CuteQueryPlan("Collection scan", null, store.RowCount, results.Count, usedNative);
        return results;
    }

    /// <summary>The managed row-by-row scan. Also the fallback whenever the accelerator declines.</summary>
    internal static void ScanManaged(
        CuteCollection collection,
        CuteExpression predicate,
        CuteParameters? parameters,
        int limit,
        List<int> results)
    {
        var store = collection.Store;
        var refs = store.Refs;

        for (var row = 0; row < refs.Length; row++)
        {
            if (refs[row].IsEmpty)
            {
                continue;
            }

            var context = new CuteEvalContext(store.Read(row), parameters);
            if (!CuteEvaluator.Test(predicate, in context))
            {
                continue;
            }

            results.Add(row);
            if (results.Count >= limit)
            {
                return;
            }
        }
    }

    /// <summary>Splits a predicate into the terms joined by top-level <c>AND</c>.</summary>
    internal static void CollectConjuncts(CuteExpression predicate, List<CuteExpression> into)
    {
        if (predicate is BinaryExpression { Operator: CuteBinaryOperator.And } and)
        {
            CollectConjuncts(and.Left, into);
            CollectConjuncts(and.Right, into);
            return;
        }

        into.Add(predicate);
    }

    private static bool TryPlanIndexLookup(
        CuteCollection collection,
        CuteExpression predicate,
        CuteParameters? parameters,
        out List<int> candidates,
        out string? indexName)
    {
        candidates = [];
        indexName = null;

        if (collection.IndexMap.Count == 0)
        {
            return false;
        }

        var conjuncts = new List<CuteExpression>(4);
        CollectConjuncts(predicate, conjuncts);

        SecondaryIndex? bestIndex = null;
        var bestKind = IndexAccess.None;
        CuteValue bestLow = CuteValue.Missing;
        CuteValue bestHigh = CuteValue.Missing;
        var bestLowInclusive = false;
        var bestHighInclusive = false;
        List<CuteValue>? bestSet = null;

        foreach (var conjunct in conjuncts)
        {
            if (!TryMatchIndexable(collection, conjunct, parameters, out var index, out var access,
                    out var low, out var lowInclusive, out var high, out var highInclusive, out var set))
            {
                continue;
            }

            // Equality on a unique index can produce at most one row, so nothing beats it; plain
            // equality beats a set membership, which beats a range.
            var rank = Rank(access, index.Unique);
            if (bestIndex is not null && rank >= Rank(bestKind, bestIndex.Unique))
            {
                continue;
            }

            bestIndex = index;
            bestKind = access;
            bestLow = low;
            bestHigh = high;
            bestLowInclusive = lowInclusive;
            bestHighInclusive = highInclusive;
            bestSet = set;
        }

        if (bestIndex is null)
        {
            return false;
        }

        switch (bestKind)
        {
            case IndexAccess.Equality:
                candidates = [.. bestIndex.Equal(bestLow)];
                break;

            case IndexAccess.Set:
                candidates = [];
                foreach (var key in bestSet!)
                {
                    candidates.AddRange(bestIndex.Equal(key));
                }

                break;

            case IndexAccess.Range:
                candidates = bestIndex.Range(bestLow, bestLowInclusive, bestHigh, bestHighInclusive);
                break;

            default:
                return false;
        }

        // A "seek" that returns most of the collection is worse than a scan: it pays for hashing
        // and a candidate list, then re-checks nearly every row anyway.
        if (candidates.Count > collection.Store.Count * 0.5)
        {
            return false;
        }

        indexName = bestIndex.Name;
        return true;
    }

    private static int Rank(IndexAccess access, bool unique) => access switch
    {
        IndexAccess.Equality when unique => 0,
        IndexAccess.Equality => 1,
        IndexAccess.Set => 2,
        IndexAccess.Range => 3,
        _ => 4,
    };

    private static bool TryMatchIndexable(
        CuteCollection collection,
        CuteExpression conjunct,
        CuteParameters? parameters,
        out SecondaryIndex index,
        out IndexAccess access,
        out CuteValue low,
        out bool lowInclusive,
        out CuteValue high,
        out bool highInclusive,
        out List<CuteValue>? set)
    {
        index = null!;
        access = IndexAccess.None;
        low = CuteValue.Missing;
        high = CuteValue.Missing;
        lowInclusive = false;
        highInclusive = false;
        set = null;

        switch (conjunct)
        {
            case BinaryExpression binary when IsComparison(binary.Operator):
            {
                if (!TryResolveSides(collection, binary, parameters, out index, out var op, out var constant))
                {
                    return false;
                }

                switch (op)
                {
                    case CuteBinaryOperator.Equal:
                        access = IndexAccess.Equality;
                        low = constant;
                        return true;

                    case CuteBinaryOperator.Greater:
                        access = IndexAccess.Range;
                        low = constant;
                        lowInclusive = false;
                        return true;

                    case CuteBinaryOperator.GreaterOrEqual:
                        access = IndexAccess.Range;
                        low = constant;
                        lowInclusive = true;
                        return true;

                    case CuteBinaryOperator.Less:
                        access = IndexAccess.Range;
                        high = constant;
                        highInclusive = false;
                        return true;

                    case CuteBinaryOperator.LessOrEqual:
                        access = IndexAccess.Range;
                        high = constant;
                        highInclusive = true;
                        return true;

                    default:
                        return false;
                }
            }

            case BetweenExpression { Negated: false } between:
            {
                if (between.Value is not PathExpression path ||
                    !TryFindIndex(collection, path.Path, out index) ||
                    !TryConstant(between.Low, parameters, out low) ||
                    !TryConstant(between.High, parameters, out high))
                {
                    return false;
                }

                access = IndexAccess.Range;
                lowInclusive = true;
                highInclusive = true;
                return true;
            }

            case InExpression { Negated: false } inExpression:
            {
                if (inExpression.Value is not PathExpression path || !TryFindIndex(collection, path.Path, out index))
                {
                    return false;
                }

                set = new List<CuteValue>(inExpression.Items.Count);
                foreach (var item in inExpression.Items)
                {
                    if (!TryConstant(item, parameters, out var value))
                    {
                        return false;
                    }

                    // `x IN @list` binds one parameter holding an array; flatten it into the set.
                    if (value.IsArray && item is ParameterExpression)
                    {
                        foreach (var element in value.AsArray.AsSpan())
                        {
                            set.Add(element);
                        }
                    }
                    else
                    {
                        set.Add(value);
                    }
                }

                access = IndexAccess.Set;
                return set.Count > 0;
            }

            default:
                return false;
        }
    }

    private static bool TryResolveSides(
        CuteCollection collection,
        BinaryExpression binary,
        CuteParameters? parameters,
        out SecondaryIndex index,
        out CuteBinaryOperator op,
        out CuteValue constant)
    {
        index = null!;
        op = binary.Operator;
        constant = CuteValue.Missing;

        if (binary.Left is PathExpression left && TryFindIndex(collection, left.Path, out index))
        {
            return TryConstant(binary.Right, parameters, out constant);
        }

        // `500 < total` means the same as `total > 500`, so the mirrored form is worth handling.
        if (binary.Right is PathExpression right && TryFindIndex(collection, right.Path, out index))
        {
            op = Mirror(binary.Operator);
            return TryConstant(binary.Left, parameters, out constant);
        }

        return false;
    }

    private static CuteBinaryOperator Mirror(CuteBinaryOperator op) => op switch
    {
        CuteBinaryOperator.Less => CuteBinaryOperator.Greater,
        CuteBinaryOperator.LessOrEqual => CuteBinaryOperator.GreaterOrEqual,
        CuteBinaryOperator.Greater => CuteBinaryOperator.Less,
        CuteBinaryOperator.GreaterOrEqual => CuteBinaryOperator.LessOrEqual,
        _ => op,
    };

    private static bool TryFindIndex(CuteCollection collection, CutePath path, out SecondaryIndex index)
    {
        foreach (var candidate in collection.IndexMap.Values)
        {
            if (string.Equals(candidate.Path.Text, path.Text, StringComparison.Ordinal))
            {
                index = candidate;
                return true;
            }
        }

        index = null!;
        return false;
    }

    private static bool TryConstant(CuteExpression expression, CuteParameters? parameters, out CuteValue value)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                value = literal.Value;
                return true;

            case ParameterExpression parameter when parameters?.Contains(parameter.Name) == true:
                value = parameters[parameter.Name];
                return true;

            case UnaryExpression { Operator: CuteUnaryOperator.Negate } negate
                when TryConstant(negate.Operand, parameters, out var inner) && inner.IsNumber:
                value = inner.Type switch
                {
                    CuteType.Int32 => CuteValue.Int32(-inner.AsInt32),
                    CuteType.Int64 => CuteValue.Int64(-inner.AsInt64),
                    CuteType.Decimal => CuteValue.Decimal(-inner.AsDecimal),
                    _ => CuteValue.Double(-inner.AsDouble),
                };

                return true;

            default:
                value = CuteValue.Missing;
                return false;
        }
    }

    private static bool IsComparison(CuteBinaryOperator op) => op is CuteBinaryOperator.Equal
        or CuteBinaryOperator.Less or CuteBinaryOperator.LessOrEqual
        or CuteBinaryOperator.Greater or CuteBinaryOperator.GreaterOrEqual;

    private enum IndexAccess
    {
        None,
        Equality,
        Set,
        Range,
    }
}
