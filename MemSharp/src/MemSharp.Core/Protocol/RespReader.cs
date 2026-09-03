using System.Buffers;
using System.Text;

namespace MemSharp.Protocol;

/// <summary>
/// Parses inbound RESP from a <see cref="ReadOnlySequence{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reads directly from the pipe's sequence and reports how much it consumed, so a partial command
/// left in the buffer is simply not consumed and gets re-parsed once more bytes arrive. That is
/// what makes the server correct under TCP segmentation - the original engine assumed one command
/// per socket read, which silently corrupted any client that pipelined or any command that spanned
/// a packet boundary.
/// </para>
/// <para>
/// Inline commands (a bare <c>SET a b\r\n</c>, no RESP framing) are accepted too, so a human can
/// drive the server from netcat.
/// </para>
/// </remarks>
public static class RespReader
{
    /// <summary>Longest command array accepted, as a guard against a hostile length prefix.</summary>
    public const int MaxArguments = 1024 * 1024;

    /// <summary>Longest bulk string accepted, in bytes.</summary>
    public const int MaxBulkLength = 512 * 1024 * 1024;

    /// <summary>
    /// Tries to parse one command.
    /// </summary>
    /// <param name="buffer">Bytes received so far.</param>
    /// <param name="command">The parsed argument vector, argument 0 being the command name.</param>
    /// <param name="consumed">How many bytes the command occupied. Only meaningful when this returns true.</param>
    /// <returns>False when the buffer holds only part of a command; call again with more bytes.</returns>
    /// <exception cref="MemSharpCommandException">The bytes are not valid RESP.</exception>
    public static bool TryParseCommand(in ReadOnlySequence<byte> buffer, out string[] command, out long consumed)
    {
        command = Array.Empty<string>();
        consumed = 0;
        if (buffer.IsEmpty) return false;

        var reader = new SequenceReader<byte>(buffer);
        byte prefix = buffer.FirstSpan.Length > 0 ? buffer.FirstSpan[0] : Peek(buffer);

        if (prefix != (byte)'*')
        {
            if (!reader.TryReadTo(out ReadOnlySequence<byte> line, "\r\n"u8, advancePastDelimiter: true)) return false;

            string text = Decode(line);
            consumed = reader.Consumed;
            command = SplitInline(text);
            return true;
        }

        reader.Advance(1);
        if (!TryReadInteger(ref reader, out long count)) return false;
        if (count < 0) { consumed = reader.Consumed; command = Array.Empty<string>(); return true; }
        if (count > MaxArguments) throw new MemSharpCommandException($"command has too many arguments ({count})");

        var arguments = new string[count];
        for (long i = 0; i < count; i++)
        {
            if (!reader.TryRead(out byte marker)) return false;
            if (marker != (byte)'$') throw new MemSharpCommandException("expected a bulk string in the command array");

            if (!TryReadInteger(ref reader, out long length)) return false;
            if (length < 0) { arguments[i] = string.Empty; continue; }
            if (length > MaxBulkLength) throw new MemSharpCommandException($"bulk string too long ({length} bytes)");

            // The payload plus its trailing CRLF must both be present; otherwise the command is
            // incomplete and must be re-parsed from the start when more bytes land.
            if (reader.Remaining < length + 2) return false;

            var payload = reader.Sequence.Slice(reader.Position, length);
            arguments[i] = Decode(payload);
            reader.Advance(length + 2);
        }

        consumed = reader.Consumed;
        command = arguments;
        return true;
    }

    /// <summary>Parses a full reply from a complete buffer - used by the client.</summary>
    public static bool TryParseValue(ref SequenceReader<byte> reader, out RespValue? value)
    {
        value = null;
        if (!reader.TryRead(out byte marker)) return false;

        switch (marker)
        {
            case (byte)'+':
                if (!reader.TryReadTo(out ReadOnlySequence<byte> status, "\r\n"u8, true)) return false;
                value = RespValue.Status(Decode(status));
                return true;

            case (byte)'-':
                if (!reader.TryReadTo(out ReadOnlySequence<byte> error, "\r\n"u8, true)) return false;
                string text = Decode(error);
                int space = text.IndexOf(' ');
                value = space > 0
                    ? RespValue.Error(text[..space], text[(space + 1)..])
                    : RespValue.Error("ERR", text);
                return true;

            case (byte)':':
                if (!TryReadInteger(ref reader, out long number)) return false;
                value = RespValue.Number64(number);
                return true;

            case (byte)'$':
            {
                if (!TryReadInteger(ref reader, out long length)) return false;
                if (length < 0) { value = RespValue.Null; return true; }
                if (reader.Remaining < length + 2) return false;

                var payload = reader.Sequence.Slice(reader.Position, length);
                value = RespValue.Bulk(Decode(payload));
                reader.Advance(length + 2);
                return true;
            }

            case (byte)'*':
            {
                if (!TryReadInteger(ref reader, out long count)) return false;
                if (count < 0) { value = RespValue.Array(null!); return true; }

                var items = new RespValue[count];
                for (long i = 0; i < count; i++)
                {
                    if (!TryParseValue(ref reader, out var item) || item is null) return false;
                    items[i] = item;
                }
                value = RespValue.Array(items);
                return true;
            }

            default:
                throw new MemSharpCommandException($"unknown RESP type marker '{(char)marker}'");
        }
    }

    /// <summary>Splits an inline command, honouring double-quoted arguments.</summary>
    private static string[] SplitInline(string line)
    {
        var parts = new List<string>();
        var builder = new StringBuilder();
        bool quoted = false;

        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (builder.Length > 0) { parts.Add(builder.ToString()); builder.Clear(); }
                continue;
            }
            builder.Append(c);
        }
        if (builder.Length > 0) parts.Add(builder.ToString());
        return parts.ToArray();
    }

    private static bool TryReadInteger(ref SequenceReader<byte> reader, out long value)
    {
        value = 0;
        if (!reader.TryReadTo(out ReadOnlySequence<byte> line, "\r\n"u8, advancePastDelimiter: true)) return false;

        bool negative = false;
        bool any = false;
        foreach (var segment in line)
        {
            foreach (byte b in segment.Span)
            {
                if (b == (byte)'-' && !any) { negative = true; continue; }
                if (b < (byte)'0' || b > (byte)'9') throw new MemSharpCommandException("malformed length prefix");
                value = value * 10 + (b - (byte)'0');
                any = true;
            }
        }

        if (!any) throw new MemSharpCommandException("empty length prefix");
        if (negative) value = -value;
        return true;
    }

    private static string Decode(in ReadOnlySequence<byte> sequence)
    {
        if (sequence.IsSingleSegment) return Encoding.UTF8.GetString(sequence.FirstSpan);

        // A value split across pipe segments is uncommon but must still decode correctly; a stack
        // buffer keeps the small case off the heap.
        int length = checked((int)sequence.Length);
        byte[]? rented = length > 256 ? ArrayPool<byte>.Shared.Rent(length) : null;
        try
        {
            Span<byte> span = rented is not null ? rented.AsSpan(0, length) : stackalloc byte[length];
            sequence.CopyTo(span);
            return Encoding.UTF8.GetString(span);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static byte Peek(in ReadOnlySequence<byte> buffer)
    {
        foreach (var segment in buffer)
        {
            if (segment.Length > 0) return segment.Span[0];
        }
        return 0;
    }
}
