using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace MemSharp.Protocol;

/// <summary>
/// Serialises <see cref="RespValue"/> replies straight into an <see cref="IBufferWriter{T}"/>.
/// </summary>
/// <remarks>
/// Writes UTF-8 bytes into the pipe's own buffer - no intermediate <c>string</c>, no
/// <c>Encoding.GetBytes</c> allocation, no <see cref="MemoryStream"/>. On a reply-heavy workload
/// that is the difference between allocating a few hundred bytes per command and allocating none.
/// Integers go through <see cref="Utf8Formatter"/> for the same reason.
/// </remarks>
public static class RespWriter
{
    private static ReadOnlySpan<byte> Crlf => "\r\n"u8;

    /// <summary>Writes one reply, including any nested elements.</summary>
    public static void Write(IBufferWriter<byte> writer, RespValue value)
    {
        switch (value.Kind)
        {
            case RespKind.SimpleString:
                WriteByte(writer, (byte)'+');
                WriteUtf8(writer, value.Text ?? string.Empty);
                WriteRaw(writer, Crlf);
                break;

            case RespKind.Error:
                WriteByte(writer, (byte)'-');
                WriteUtf8(writer, value.Text ?? "ERR");
                WriteRaw(writer, Crlf);
                break;

            case RespKind.Integer:
                WriteByte(writer, (byte)':');
                WriteInteger(writer, value.Integer);
                WriteRaw(writer, Crlf);
                break;

            case RespKind.Double:
                WriteBulk(writer, value.Number.ToString("R", CultureInfo.InvariantCulture));
                break;

            case RespKind.BulkString:
                WriteBulk(writer, value.Text);
                break;

            case RespKind.Array:
                if (value.Items is null)
                {
                    WriteRaw(writer, "*-1\r\n"u8);
                    break;
                }
                WriteByte(writer, (byte)'*');
                WriteInteger(writer, value.Items.Length);
                WriteRaw(writer, Crlf);
                foreach (var item in value.Items) Write(writer, item);
                break;
        }
    }

    /// <summary>Writes a bulk string, or <c>$-1</c> for <c>null</c>.</summary>
    public static void WriteBulk(IBufferWriter<byte> writer, string? text)
    {
        if (text is null)
        {
            WriteRaw(writer, "$-1\r\n"u8);
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(text);
        WriteByte(writer, (byte)'$');
        WriteInteger(writer, byteCount);
        WriteRaw(writer, Crlf);

        var span = writer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(text, span);
        writer.Advance(byteCount);

        WriteRaw(writer, Crlf);
    }

    /// <summary>
    /// Writes a command as a RESP array of bulk strings - the request form, used by the client and
    /// by the append-only log.
    /// </summary>
    public static void WriteCommand(IBufferWriter<byte> writer, string command, params string[] arguments)
    {
        WriteByte(writer, (byte)'*');
        WriteInteger(writer, arguments.Length + 1);
        WriteRaw(writer, Crlf);

        WriteBulk(writer, command);
        foreach (var argument in arguments) WriteBulk(writer, argument);
    }

    private static void WriteInteger(IBufferWriter<byte> writer, long value)
    {
        // 20 digits covers long.MinValue including the sign.
        var span = writer.GetSpan(20);
        Utf8Formatter.TryFormat(value, span, out int written);
        writer.Advance(written);
    }

    private static void WriteUtf8(IBufferWriter<byte> writer, string text)
    {
        int byteCount = Encoding.UTF8.GetByteCount(text);
        var span = writer.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(text, span);
        writer.Advance(byteCount);
    }

    private static void WriteRaw(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
    {
        var span = writer.GetSpan(bytes.Length);
        bytes.CopyTo(span);
        writer.Advance(bytes.Length);
    }

    private static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        var span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }
}
