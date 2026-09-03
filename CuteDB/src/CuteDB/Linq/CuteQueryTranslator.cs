using System.Linq.Expressions;
using System.Reflection;
using CuteDB.Mapping;
using CuteDB.Query;

namespace CuteDB.Linq;

/// <summary>
/// Walks a LINQ method chain and builds the query it describes.
/// </summary>
/// <remarks>
/// <para>
/// A chain such as <c>orders.Where(…).OrderByDescending(…).Take(10)</c> reaches the provider
/// inside out, as <c>Take(OrderByDescending(Where(source)))</c>, so translation recurses into the
/// source before applying the outer operator. Everything accumulates into one
/// <see cref="CuteQueryModel"/>, which becomes a single statement — the engine is never asked for
/// rows it will not return.
/// </para>
/// <para>
/// Anything without a CuteQL equivalent raises <see cref="CuteTranslationException"/> naming what
/// was not understood. The one exception is a <c>Select</c> whose body cannot be expressed, which
/// falls back to shaping in memory <em>after</em> the engine has filtered, sorted and paged.
/// </para>
/// </remarks>
internal sealed class CuteQueryTranslator(string collection, Type documentType, CuteNamingPolicy naming)
{
    /// <summary>Translates a whole expression tree into a runnable model.</summary>
    internal CuteQueryModel Translate(Expression expression)
    {
        var model = new CuteQueryModel
        {
            Collection = collection,
            DocumentType = documentType,
            Naming = naming,
            ResultType = documentType,
        };

        Visit(expression, model);

        model.Materialize ??= BuildDocumentMaterializer(model.ResultType);
        return model;
    }

    private void Visit(Expression expression, CuteQueryModel model)
    {
        switch (expression)
        {
            case MethodCallExpression call:
                VisitCall(call, model);
                return;

            case ConstantExpression:
                // The root: the queryable itself.
                return;

            default:
                throw new CuteTranslationException($"A query cannot start from {expression.NodeType}.");
        }
    }

    private void VisitCall(MethodCallExpression call, CuteQueryModel model)
    {
        if (call.Method.DeclaringType != typeof(Queryable) && call.Method.DeclaringType != typeof(Enumerable))
        {
            throw new CuteTranslationException(
                $"'{call.Method.DeclaringType?.Name}.{call.Method.Name}' is not a query operator CuteDB understands.");
        }

        // The source is translated first, so operators apply in the order they were written.
        Visit(call.Arguments[0], model);

        var name = call.Method.Name;
        switch (name)
        {
            case nameof(Queryable.Where):
                ApplyWhere(call, model);
                return;

            case nameof(Queryable.Select):
                ApplySelect(call, model);
                return;

            case nameof(Queryable.OrderBy):
                ApplyOrderBy(call, model, descending: false, reset: true);
                return;

            case nameof(Queryable.OrderByDescending):
                ApplyOrderBy(call, model, descending: true, reset: true);
                return;

            case nameof(Queryable.ThenBy):
                ApplyOrderBy(call, model, descending: false, reset: false);
                return;

            case nameof(Queryable.ThenByDescending):
                ApplyOrderBy(call, model, descending: true, reset: false);
                return;

            case nameof(Queryable.Take):
                model.Limit = Math.Min(model.Limit ?? int.MaxValue, Count(call.Arguments[1]));
                return;

            case nameof(Queryable.Skip):
                model.Offset += Count(call.Arguments[1]);
                return;

            case nameof(Queryable.Distinct):
                model.Distinct = true;
                return;

            case nameof(Queryable.GroupBy):
                ApplyGroupBy(call, model);
                return;

            case nameof(Queryable.Reverse):
                if (model.OrderBy.Count == 0)
                {
                    throw new CuteTranslationException("Reverse needs an OrderBy to reverse.");
                }

                var reversed = model.OrderBy.Select(o => o with { Descending = !o.Descending }).ToList();
                model.OrderBy.Clear();
                model.OrderBy.AddRange(reversed);
                return;

            case nameof(Queryable.First):
            case nameof(Queryable.FirstOrDefault):
            case nameof(Queryable.Single):
            case nameof(Queryable.SingleOrDefault):
            case nameof(Queryable.Last):
            case nameof(Queryable.LastOrDefault):
            case nameof(Queryable.Any):
            case nameof(Queryable.All):
            case nameof(Queryable.Count):
            case nameof(Queryable.LongCount):
                ApplyTerminal(call, model, name);
                return;

            case nameof(Queryable.Sum):
            case nameof(Queryable.Average):
            case nameof(Queryable.Min):
            case nameof(Queryable.Max):
                ApplyAggregate(call, model, name);
                return;

            case nameof(Queryable.ElementAt):
            case nameof(Queryable.ElementAtOrDefault):
                model.Offset += Count(call.Arguments[1]);
                model.Limit = 1;
                model.Shape = name == nameof(Queryable.ElementAt)
                    ? CuteResultShape.ElementAt
                    : CuteResultShape.ElementAtOrDefault;

                return;

            default:
                throw new CuteTranslationException(
                    $"'{name}' is not supported. Supported: Where, Select, OrderBy/ThenBy (and Descending), " +
                    "Take, Skip, Distinct, GroupBy, Reverse, First/Single/Last (and OrDefault), ElementAt, " +
                    "Any, All, Count, LongCount, Sum, Average, Min, Max.");
        }
    }

    // -------------------------------------------------------------------------------------

    private void ApplyWhere(MethodCallExpression call, CuteQueryModel model)
    {
        var lambda = Lambda(call.Arguments[1]);
        var translator = TranslatorFor(lambda, model);
        var predicate = translator.TranslatePredicate(lambda.Body);

        // A filter after a GroupBy is a HAVING; before one it is a WHERE. Which is which is
        // decided by whether grouping has happened yet, exactly as in SQL.
        if (model.GroupBy.Count > 0)
        {
            model.Having = Combine(model.Having, predicate);
        }
        else
        {
            model.Where = Combine(model.Where, predicate);
        }
    }

    private void ApplyOrderBy(MethodCallExpression call, CuteQueryModel model, bool descending, bool reset)
    {
        var lambda = Lambda(call.Arguments[1]);

        if (reset)
        {
            model.OrderBy.Clear();
        }

        // Ordering happens after projection, so a sort key that names a projected column has to be
        // rendered as that column. Inlining the expression it was built from would sort by a field
        // the projected rows no longer carry — which silently returns them unsorted.
        var key = OrderByAlias(lambda, model) is { } alias
            ? new PathExpression(CutePath.Parse(alias))
            : TranslatorFor(lambda, model).Translate(lambda.Body);

        model.OrderBy.Add(new CuteOrdering(key, descending));
    }

    /// <summary>The projection alias a sort key names, or null when it does not name one.</summary>
    private static string? OrderByAlias(LambdaExpression lambda, CuteQueryModel model)
    {
        if (model.Projections.Count == 0 || lambda.Parameters.Count != 1)
        {
            return null;
        }

        var body = CuteExpressionTranslator.Strip(lambda.Body);
        var parameter = lambda.Parameters[0];

        // `.Select(o => o.City).OrderBy(c => c)`: the parameter is the single projected value.
        if (body == parameter)
        {
            return model.Projections is [{ Alias: "value" }] ? "value" : null;
        }

        if (body is MemberExpression member && CuteExpressionTranslator.Strip(member.Expression!) == parameter)
        {
            return model.Projections.Any(p => p.Alias == member.Member.Name) ? member.Member.Name : null;
        }

        return null;
    }

    private void ApplyGroupBy(MethodCallExpression call, CuteQueryModel model)
    {
        if (call.Arguments.Count > 2)
        {
            throw new CuteTranslationException(
                "GroupBy with an element or result selector is not supported. " +
                "Group by the key, then Select the aggregates you want.");
        }

        var lambda = Lambda(call.Arguments[1]);
        var translator = TranslatorFor(lambda, model);
        var body = CuteExpressionTranslator.Strip(lambda.Body);

        model.GroupBy.Clear();

        // A composite key — GroupBy(o => new { o.City, o.Status }) — becomes several grouping
        // expressions, which is what CuteQL's GROUP BY takes anyway.
        if (body is NewExpression composite && composite.Arguments.Count > 0)
        {
            foreach (var argument in composite.Arguments)
            {
                model.GroupBy.Add(translator.Translate(argument));
            }

            model.GroupKeyMembers = [.. composite.Members?.Select(m => m.Name) ?? []];
        }
        else
        {
            model.GroupBy.Add(translator.Translate(body));
            model.GroupKeyMembers = [];
        }

        model.GroupKeyType = lambda.Body.Type;
        model.GroupSourceParameter = lambda.Parameters[0];
    }

    private void ApplySelect(MethodCallExpression call, CuteQueryModel model)
    {
        var lambda = Lambda(call.Arguments[1]);

        if (model.GroupBy.Count > 0)
        {
            ApplyGroupProjection(lambda, model);
            return;
        }

        var translator = TranslatorFor(lambda, model);
        var body = CuteExpressionTranslator.Strip(lambda.Body);

        if (TryPushDownProjection(body, translator, model, lambda))
        {
            return;
        }

        // The body has no CuteQL equivalent, so the engine returns whole documents and the shaping
        // happens after mapping. Filtering, ordering and paging still ran on the engine.
        model.Projections.Clear();
        model.ProjectionAliases.Clear();
        model.ClientSelector = lambda.Compile();
        model.ClientSelectorInput = model.ResultType;
        model.ResultType = lambda.ReturnType;
        model.Materialize = null;
    }

    /// <summary>
    /// Pushes a selector into the statement, so only the fields asked for come back.
    /// </summary>
    private bool TryPushDownProjection(
        Expression body,
        CuteExpressionTranslator translator,
        CuteQueryModel model,
        LambdaExpression lambda)
    {
        var projections = new List<CuteProjection>();
        var aliases = new Dictionary<string, CuteExpression>(StringComparer.Ordinal);
        Func<CuteObject, object?> materializer;

        switch (body)
        {
            // new { A = …, B = … }
            case NewExpression anonymous when anonymous.Members is { Count: > 0 }:
            {
                for (var i = 0; i < anonymous.Arguments.Count; i++)
                {
                    if (!TryTranslate(translator, anonymous.Arguments[i], out var translated))
                    {
                        return false;
                    }

                    var alias = anonymous.Members[i].Name;
                    projections.Add(new CuteProjection(translated, alias));
                    aliases[alias] = translated;
                }

                materializer = BuildConstructorMaterializer(
                    anonymous.Constructor!,
                    [.. anonymous.Members.Select(m => m.Name)],
                    [.. anonymous.Constructor!.GetParameters().Select(p => p.ParameterType)],
                    model.Naming);

                break;
            }

            // new Dto { X = …, Y = … }
            case MemberInitExpression init when init.NewExpression.Arguments.Count == 0:
            {
                var assignments = new List<MemberAssignment>();
                foreach (var binding in init.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                    {
                        return false;
                    }

                    if (!TryTranslate(translator, assignment.Expression, out var translated))
                    {
                        return false;
                    }

                    projections.Add(new CuteProjection(translated, assignment.Member.Name));
                    aliases[assignment.Member.Name] = translated;
                    assignments.Add(assignment);
                }

                materializer = BuildMemberInitMaterializer(init.NewExpression.Constructor, assignments, model.Naming);
                break;
            }

            // o => o.Total, or any single translatable value
            default:
            {
                if (!TryTranslate(translator, body, out var translated))
                {
                    return false;
                }

                projections.Add(new CuteProjection(translated, "value"));
                aliases["value"] = translated;

                var resultType = lambda.ReturnType;
                materializer = row => CuteMapper.FromValue(row["value"], resultType, model.Naming);
                break;
            }
        }

        model.Projections.Clear();
        model.Projections.AddRange(projections);
        model.ProjectionAliases.Clear();
        foreach (var (key, value) in aliases)
        {
            model.ProjectionAliases[key] = value;
        }

        model.ResultType = lambda.ReturnType;
        model.Materialize = materializer;
        model.ClientSelector = null;
        return true;
    }

    /// <summary>Projects the result of a GroupBy: key parts and aggregates.</summary>
    private void ApplyGroupProjection(LambdaExpression lambda, CuteQueryModel model)
    {
        var grouping = lambda.Parameters[0];
        var body = CuteExpressionTranslator.Strip(lambda.Body);

        var projections = new List<CuteProjection>();
        var names = new List<string>();
        var types = new List<Type>();

        void Add(string alias, Expression source, Type type)
        {
            projections.Add(new CuteProjection(TranslateGroupPart(source, grouping, model), alias));
            names.Add(alias);
            types.Add(type);
        }

        switch (body)
        {
            case NewExpression anonymous when anonymous.Members is { Count: > 0 }:
            {
                for (var i = 0; i < anonymous.Arguments.Count; i++)
                {
                    Add(anonymous.Members[i].Name, anonymous.Arguments[i], anonymous.Constructor!.GetParameters()[i].ParameterType);
                }

                model.Materialize = BuildConstructorMaterializer(anonymous.Constructor!, [.. names], [.. types], model.Naming);
                break;
            }

            case MemberInitExpression init when init.NewExpression.Arguments.Count == 0:
            {
                var assignments = new List<MemberAssignment>();
                foreach (var binding in init.Bindings)
                {
                    if (binding is not MemberAssignment assignment)
                    {
                        throw new CuteTranslationException("Only simple property assignments are supported in a group projection.");
                    }

                    projections.Add(new CuteProjection(
                        TranslateGroupPart(assignment.Expression, grouping, model), assignment.Member.Name));

                    assignments.Add(assignment);
                }

                model.Materialize = BuildMemberInitMaterializer(init.NewExpression.Constructor, assignments, model.Naming);
                break;
            }

            default:
            {
                projections.Add(new CuteProjection(TranslateGroupPart(body, grouping, model), "value"));
                var resultType = lambda.ReturnType;
                model.Materialize = row => CuteMapper.FromValue(row["value"], resultType, model.Naming);
                break;
            }
        }

        model.Projections.Clear();
        model.Projections.AddRange(projections);
        model.ResultType = lambda.ReturnType;

        // A grouped projection publishes no aliases for a later Where: a HAVING is evaluated over
        // the group, where the projected columns do not yet exist. Ordering by one is handled in
        // ApplyOrderBy, which names the column rather than re-deriving it.
        model.ProjectionAliases.Clear();
    }

    /// <summary>
    /// Translates one part of a grouped projection: a piece of the key, or an aggregate.
    /// </summary>
    private CuteExpression TranslateGroupPart(Expression expression, System.Linq.Expressions.ParameterExpression grouping, CuteQueryModel model)
    {
        expression = CuteExpressionTranslator.Strip(expression);

        // g.Key, or a member of a composite key.
        if (expression is MemberExpression { Member.Name: "Key" } key && key.Expression == grouping)
        {
            return model.GroupBy.Count == 1
                ? model.GroupBy[0]
                : throw new CuteTranslationException(
                    "The key of a composite GroupBy has to be projected member by member, such as g.Key.City.");
        }

        if (expression is MemberExpression { Expression: MemberExpression { Member.Name: "Key" } inner } part
            && inner.Expression == grouping)
        {
            var index = model.GroupKeyMembers.IndexOf(part.Member.Name);
            return index >= 0
                ? model.GroupBy[index]
                : throw new CuteTranslationException($"'{part.Member.Name}' is not part of the grouping key.");
        }

        // g.Count(), g.Sum(x => x.Total), and friends.
        if (expression is MethodCallExpression call && IsOverGrouping(call, grouping))
        {
            var function = call.Method.Name switch
            {
                nameof(Enumerable.Count) or nameof(Enumerable.LongCount) => "COUNT",
                nameof(Enumerable.Sum) => "SUM",
                nameof(Enumerable.Average) => "AVG",
                nameof(Enumerable.Min) => "MIN",
                nameof(Enumerable.Max) => "MAX",
                _ => throw new CuteTranslationException(
                    $"'{call.Method.Name}' is not an aggregate CuteQL has. Use Count, Sum, Average, Min or Max."),
            };

            if (function == "COUNT" && call.Arguments.Count == 1)
            {
                return new FunctionExpression("COUNT", [new StarExpression()]);
            }

            if (call.Arguments.Count < 2)
            {
                throw new CuteTranslationException($"{function} needs a selector, such as g.Sum(x => x.Total).");
            }

            var selector = Lambda(call.Arguments[1]);
            var translator = new CuteExpressionTranslator(model.Naming, selector.Parameters);
            return new FunctionExpression(function, [translator.Translate(selector.Body)]);
        }

        throw new CuteTranslationException(
            "A grouped projection may use the key and the aggregates Count, Sum, Average, Min and Max. " +
            $"'{expression}' is neither.");
    }

    private static bool IsOverGrouping(MethodCallExpression call, System.Linq.Expressions.ParameterExpression grouping)
        => call.Arguments.Count > 0 && CuteExpressionTranslator.Strip(call.Arguments[0]) == grouping;

    private void ApplyTerminal(MethodCallExpression call, CuteQueryModel model, string name)
    {
        // The overloads that take a predicate mean "…the ones matching this", so it is a filter
        // followed by the terminal.
        if (call.Arguments.Count == 2)
        {
            var lambda = Lambda(call.Arguments[1]);
            var translator = TranslatorFor(lambda, model);
            var predicate = translator.TranslatePredicate(lambda.Body);

            if (name == nameof(Queryable.All))
            {
                // All(p) is "nothing fails p", which is the question the engine can answer.
                predicate = new Query.UnaryExpression(CuteUnaryOperator.Not, predicate);
            }

            if (model.GroupBy.Count > 0)
            {
                model.Having = Combine(model.Having, predicate);
            }
            else
            {
                model.Where = Combine(model.Where, predicate);
            }
        }
        else if (name == nameof(Queryable.All))
        {
            throw new CuteTranslationException("All needs a predicate.");
        }

        model.Shape = name switch
        {
            nameof(Queryable.First) => CuteResultShape.First,
            nameof(Queryable.FirstOrDefault) => CuteResultShape.FirstOrDefault,
            nameof(Queryable.Single) => CuteResultShape.Single,
            nameof(Queryable.SingleOrDefault) => CuteResultShape.SingleOrDefault,
            nameof(Queryable.Last) => CuteResultShape.Last,
            nameof(Queryable.LastOrDefault) => CuteResultShape.LastOrDefault,
            nameof(Queryable.Any) => CuteResultShape.Any,
            nameof(Queryable.All) => CuteResultShape.All,
            nameof(Queryable.Count) => CuteResultShape.Count,
            _ => CuteResultShape.LongCount,
        };

        // Last has to see every row in order; the engine cannot answer it with LIMIT 1 unless the
        // ordering is reversed, which is what happens here.
        if (model.Shape is CuteResultShape.Last or CuteResultShape.LastOrDefault && model.OrderBy.Count > 0)
        {
            var reversed = model.OrderBy.Select(o => o with { Descending = !o.Descending }).ToList();
            model.OrderBy.Clear();
            model.OrderBy.AddRange(reversed);
            model.Limit = 1;
        }
    }

    private void ApplyAggregate(MethodCallExpression call, CuteQueryModel model, string name)
    {
        if (call.Arguments.Count == 2)
        {
            var lambda = Lambda(call.Arguments[1]);
            var translator = TranslatorFor(lambda, model);

            model.Projections.Clear();
            model.Projections.Add(new CuteProjection(translator.Translate(lambda.Body), "value"));
        }
        else if (model.Projections.Count != 1)
        {
            throw new CuteTranslationException(
                $"{name} needs to know what to aggregate. Pass a selector, or Select one value first.");
        }

        model.Shape = name switch
        {
            nameof(Queryable.Sum) => CuteResultShape.Sum,
            nameof(Queryable.Average) => CuteResultShape.Average,
            nameof(Queryable.Min) => CuteResultShape.Min,
            _ => CuteResultShape.Max,
        };

        model.ResultType = call.Method.ReturnType;
    }

    // -------------------------------------------------------------------------------------
    // materializers
    // -------------------------------------------------------------------------------------

    private Func<CuteObject, object?> BuildDocumentMaterializer(Type type)
    {
        var policy = naming;

        if (type == typeof(CuteDocument))
        {
            return row => new CuteDocument(row, assignId: false);
        }

        if (type == typeof(CuteObject))
        {
            return row => row;
        }

        return row => CuteMapper.FromValue(CuteValue.Object(row), type, policy);
    }

    private static Func<CuteObject, object?> BuildConstructorMaterializer(
        ConstructorInfo constructor,
        string[] aliases,
        Type[] parameterTypes,
        CuteNamingPolicy naming)
        => row =>
        {
            var arguments = new object?[aliases.Length];
            for (var i = 0; i < aliases.Length; i++)
            {
                arguments[i] = CuteMapper.FromValue(row[aliases[i]], parameterTypes[i], naming);
            }

            return constructor.Invoke(arguments);
        };

    private static Func<CuteObject, object?> BuildMemberInitMaterializer(
        ConstructorInfo? constructor,
        List<MemberAssignment> assignments,
        CuteNamingPolicy naming)
    {
        var setters = assignments
            .Select(a => (a.Member.Name, Type: a.Member is PropertyInfo p ? p.PropertyType : ((FieldInfo)a.Member).FieldType, a.Member))
            .ToArray();

        return row =>
        {
            var instance = constructor?.Invoke(null)
                ?? throw new CuteTranslationException("The projected type needs a parameterless constructor.");

            foreach (var (name, type, member) in setters)
            {
                var value = CuteMapper.FromValue(row[name], type, naming);
                switch (member)
                {
                    case PropertyInfo property:
                        property.SetValue(instance, value);
                        break;
                    case FieldInfo field:
                        field.SetValue(instance, value);
                        break;
                }
            }

            return instance;
        };
    }

    // -------------------------------------------------------------------------------------
    // helpers
    // -------------------------------------------------------------------------------------

    private CuteExpressionTranslator TranslatorFor(LambdaExpression lambda, CuteQueryModel model)
    {
        var translator = new CuteExpressionTranslator(
            model.Naming,
            lambda.Parameters,
            model.ProjectionAliases.Count > 0 ? model.ProjectionAliases : null);

        // Once grouping has happened the lambda's parameter is the grouping, so a filter on it is
        // a HAVING over aggregates rather than a predicate over document fields.
        if (model.GroupBy.Count > 0 && lambda.Parameters.Count == 1)
        {
            var grouping = lambda.Parameters[0];
            translator.GroupResolver = expression =>
            {
                try
                {
                    return TranslateGroupPart(expression, grouping, model);
                }
                catch (CuteTranslationException)
                {
                    // Not a group part — the ordinary rules apply.
                    return null;
                }
            };
        }

        return translator;
    }

    private static bool TryTranslate(CuteExpressionTranslator translator, Expression expression, out CuteExpression result)
    {
        try
        {
            result = translator.Translate(expression);
            return true;
        }
        catch (CuteTranslationException)
        {
            result = null!;
            return false;
        }
    }

    private static LambdaExpression Lambda(Expression expression)
        => (LambdaExpression)CuteExpressionTranslator.Strip(expression);

    private static int Count(Expression expression)
        => Convert.ToInt32(CuteExpressionTranslator.Evaluate(expression), System.Globalization.CultureInfo.InvariantCulture);

    private static CuteExpression Combine(CuteExpression? existing, CuteExpression addition)
        => existing is null ? addition : new Query.BinaryExpression(CuteBinaryOperator.And, existing, addition);
}
