using CuteDB.Query;

namespace CuteDB.Native;

/// <summary>Opcodes of the predicate bytecode the native scanner executes.</summary>
/// <remarks>
/// These values are shared with <c>native/cutedb-core/src/vm.rs</c>. Both sides must change
/// together, which <see cref="PredicateProgram.AbiVersion"/> guards at load time.
/// </remarks>
internal enum PredicateOp : byte
{
    PushPath = 0x01,
    PushConst = 0x02,

    Equal = 0x10,
    NotEqual = 0x11,
    Less = 0x12,
    LessOrEqual = 0x13,
    Greater = 0x14,
    GreaterOrEqual = 0x15,
    In = 0x16,
    Like = 0x17,
    NotLike = 0x18,
    Between = 0x19,
    NotBetween = 0x1A,
    IsNull = 0x1B,
    IsNotNull = 0x1C,
    IsMissing = 0x1D,
    IsNotMissing = 0x1E,
    NotIn = 0x1F,

    And = 0x20,
    Or = 0x21,
    Not = 0x22,

    Return = 0xFF,
}

/// <summary>
/// A predicate compiled to bytecode, ready to be handed to the native scanner.
/// </summary>
/// <remarks>
/// <para>
/// The bytecode exists so the scan loop can run entirely on the other side of the P/Invoke
/// boundary. Calling back into managed code once per document would cost more than the comparison
/// it performs; shipping the whole predicate across once, then letting Rust walk a million
/// documents without returning, is what makes the accelerator worth having at all.
/// </para>
/// <para>
/// Only a subset of CuteQL compiles: paths, constants, the six comparisons, <c>IN</c>,
/// <c>LIKE</c>, <c>BETWEEN</c>, the null and missing tests, and the boolean connectives. Anything
/// else — arithmetic, function calls, parameters that were never bound — makes
/// <see cref="TryCompile"/> return false, and the caller runs the managed evaluator instead. That
/// keeps the Rust side small enough to be obviously correct, and it means an exotic query is
/// merely unaccelerated rather than unsupported.
/// </para>
/// <para>
/// One deliberate difference from the managed evaluator: <c>AND</c> and <c>OR</c> here evaluate
/// both operands rather than short-circuiting, because the compiled form pushes both before
/// combining them. The result is identical — the operands are pure — but a predicate whose right
/// side is expensive and rarely reached is one of the cases where the managed path can win.
/// </para>
/// </remarks>
internal sealed class PredicateProgram
{
    /// <summary>Bumped whenever the bytecode or its container changes shape.</summary>
    internal const uint AbiVersion = 1;

    private PredicateProgram(byte[] bytes) => Bytes = bytes;

    /// <summary>The serialised program.</summary>
    internal byte[] Bytes { get; }

    /// <summary>
    /// Compiles a predicate, or returns false when it uses something the native VM does not
    /// implement.
    /// </summary>
    internal static bool TryCompile(CuteExpression predicate, CuteParameters? parameters, out PredicateProgram program)
    {
        program = null!;

        var builder = new Builder(parameters);
        if (!builder.TryEmit(predicate))
        {
            return false;
        }

        builder.EmitOp(PredicateOp.Return);
        program = new PredicateProgram(builder.Serialize());
        return true;
    }

    private sealed class Builder(CuteParameters? parameters)
    {
        private readonly List<CutePath> _paths = [];
        private readonly List<CuteValue> _constants = [];
        private readonly List<byte> _code = [];

        internal bool TryEmit(CuteExpression expression)
        {
            switch (expression)
            {
                case BinaryExpression { Operator: CuteBinaryOperator.And } and:
                    return TryEmit(and.Left) && TryEmit(and.Right) && EmitOpTrue(PredicateOp.And);

                case BinaryExpression { Operator: CuteBinaryOperator.Or } or:
                    return TryEmit(or.Left) && TryEmit(or.Right) && EmitOpTrue(PredicateOp.Or);

                case UnaryExpression { Operator: CuteUnaryOperator.Not } not:
                    return TryEmit(not.Operand) && EmitOpTrue(PredicateOp.Not);

                case BinaryExpression binary when TryMapComparison(binary.Operator, out var op):
                    return TryEmitOperand(binary.Left) && TryEmitOperand(binary.Right) && EmitOpTrue(op);

                case BetweenExpression between:
                    return TryEmitOperand(between.Value)
                        && TryEmitOperand(between.Low)
                        && TryEmitOperand(between.High)
                        && EmitOpTrue(between.Negated ? PredicateOp.NotBetween : PredicateOp.Between);

                case InExpression inExpression:
                    return TryEmitIn(inExpression);

                case IsExpression isExpression:
                {
                    if (!TryEmitOperand(isExpression.Value))
                    {
                        return false;
                    }

                    var op = (isExpression.Missing, isExpression.Negated) switch
                    {
                        (true, false) => PredicateOp.IsMissing,
                        (true, true) => PredicateOp.IsNotMissing,
                        (false, false) => PredicateOp.IsNull,
                        (false, true) => PredicateOp.IsNotNull,
                    };

                    return EmitOpTrue(op);
                }

                default:
                    return false;
            }
        }

        internal void EmitOp(PredicateOp op) => _code.Add((byte)op);

        internal byte[] Serialize()
        {
            var writer = new CuteBufferWriter(1024);

            writer.WriteUInt32(0x50545543); // 'CUTP', little-endian.
            writer.WriteUInt32(AbiVersion);
            writer.WriteUInt32((uint)_paths.Count);
            writer.WriteUInt32((uint)_constants.Count);

            foreach (var path in _paths)
            {
                path.Encode(writer);
            }

            foreach (var constant in _constants)
            {
                var slot = writer.ReserveUInt32();
                var start = writer.Length;
                CuteBinary.Write(writer, constant);
                writer.PatchUInt32(slot, (uint)(writer.Length - start));
            }

            writer.WriteUInt32((uint)_code.Count);
            foreach (var b in _code)
            {
                writer.WriteByte(b);
            }

            var bytes = writer.ToArray();
            writer.Dispose();
            return bytes;
        }

        private bool TryEmitIn(InExpression expression)
        {
            if (!TryEmitOperand(expression.Value))
            {
                return false;
            }

            // The candidate list becomes one array constant, so the VM does a single pass over it
            // rather than executing N comparisons.
            var items = new CuteArray(expression.Items.Count);
            foreach (var item in expression.Items)
            {
                if (!TryResolveConstant(item, out var value))
                {
                    return false;
                }

                if (value.IsArray && item is ParameterExpression)
                {
                    foreach (var element in value.AsArray.AsSpan())
                    {
                        items.Add(element);
                    }
                }
                else
                {
                    items.Add(value);
                }
            }

            EmitPushConst(CuteValue.Array(items));
            return EmitOpTrue(expression.Negated ? PredicateOp.NotIn : PredicateOp.In);
        }

        private bool TryEmitOperand(CuteExpression expression)
        {
            switch (expression)
            {
                case PathExpression path:
                    if (path.Path.HasProjection)
                    {
                        return false;
                    }

                    EmitPushPath(path.Path);
                    return true;

                default:
                    if (!TryResolveConstant(expression, out var value))
                    {
                        return false;
                    }

                    EmitPushConst(value);
                    return true;
            }
        }

        private bool TryResolveConstant(CuteExpression expression, out CuteValue value)
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
                    when TryResolveConstant(negate.Operand, out var inner) && inner.IsNumber:
                    value = inner.Type switch
                    {
                        CuteType.Int32 => CuteValue.Int32(-inner.AsInt32),
                        CuteType.Int64 => CuteValue.Int64(-inner.AsInt64),
                        CuteType.Decimal => CuteValue.Decimal(-inner.AsDecimal),
                        _ => CuteValue.Double(-inner.AsDouble),
                    };

                    return true;

                case ArrayExpression array:
                {
                    var items = new CuteArray(array.Items.Count);
                    foreach (var item in array.Items)
                    {
                        if (!TryResolveConstant(item, out var element))
                        {
                            value = CuteValue.Missing;
                            return false;
                        }

                        items.Add(element);
                    }

                    value = CuteValue.Array(items);
                    return true;
                }

                default:
                    value = CuteValue.Missing;
                    return false;
            }
        }

        private void EmitPushPath(CutePath path)
        {
            var index = _paths.IndexOf(path);
            if (index < 0)
            {
                index = _paths.Count;
                _paths.Add(path);
            }

            _code.Add((byte)PredicateOp.PushPath);
            _code.Add((byte)(index & 0xFF));
            _code.Add((byte)((index >> 8) & 0xFF));
        }

        private void EmitPushConst(CuteValue value)
        {
            var index = _constants.Count;
            _constants.Add(value);

            _code.Add((byte)PredicateOp.PushConst);
            _code.Add((byte)(index & 0xFF));
            _code.Add((byte)((index >> 8) & 0xFF));
        }

        private bool EmitOpTrue(PredicateOp op)
        {
            _code.Add((byte)op);
            return true;
        }

        private static bool TryMapComparison(CuteBinaryOperator op, out PredicateOp mapped)
        {
            mapped = op switch
            {
                CuteBinaryOperator.Equal => PredicateOp.Equal,
                CuteBinaryOperator.NotEqual => PredicateOp.NotEqual,
                CuteBinaryOperator.Less => PredicateOp.Less,
                CuteBinaryOperator.LessOrEqual => PredicateOp.LessOrEqual,
                CuteBinaryOperator.Greater => PredicateOp.Greater,
                CuteBinaryOperator.GreaterOrEqual => PredicateOp.GreaterOrEqual,
                CuteBinaryOperator.Like => PredicateOp.Like,
                CuteBinaryOperator.NotLike => PredicateOp.NotLike,
                _ => default,
            };

            return mapped != default;
        }
    }
}
