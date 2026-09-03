using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CuteDB;

/// <summary>
/// Options controlling how JSON text maps onto <see cref="CuteValue"/>.
/// </summary>
public sealed record CuteJsonOptions
{
    /// <summary>The defaults: plain JSON out, doubles for fractional numbers in.</summary>
    public static CuteJsonOptions Default { get; } = new();

    /// <summary>Reads fractional numbers as <see cref="CuteType.Decimal"/> instead of doubles.</summary>
    public static CuteJsonOptions Financial { get; } = new() { PreferDecimal = true };

    /// <summary>Writes every non-JSON type in its lossless <c>$</c>-tagged form.</summary>
    public static CuteJsonOptions Lossless { get; } = new() { Extended = true, PreferDecimal = true };

    /// <summary>
    /// When true, fractional JSON numbers are read as decimals rather than doubles.
    /// </summary>
    /// <remarks>
    /// JSON has one number type and every other parser resolves it to a double, so that is the
    /// default here too. Importing money is the case where it is the wrong answer — 0.1 + 0.2 has
    /// to be 0.3 on an invoice — so imports of financial data should turn this on. It only affects
    /// numbers that actually have a fraction or exponent; integers are unaffected either way.
    /// </remarks>
    public bool PreferDecimal { get; init; }

    /// <summary>
    /// When true, values with no JSON equivalent are written as tagged objects
    /// (<c>{"$date":…}</c>, <c>{"$binary":…}</c>, <c>{"$guid":…}</c>, <c>{"$id":…}</c>,
    /// <c>{"$decimal":…}</c>) so that a round trip through text is lossless.
    /// </summary>
    /// <remarks>
    /// The plain form is much nicer to read and is what the CLI, the REST API and the exports
    /// produce by default; it renders those values as ISO-8601 or hex strings, which read back as
    /// strings. Turn this on when the JSON is a backup rather than something a person will look at.
    /// </remarks>
    public bool Extended { get; init; }

    /// <summary>Indents the output across multiple lines.</summary>
    public bool Indented { get; init; }
}

/// <summary>
/// Converts between JSON text and <see cref="CuteValue"/>.
/// </summary>
/// <remarks>
/// Parsing runs on <see cref="Utf8JsonReader"/> and writing on <see cref="Utf8JsonWriter"/>, so
/// CuteDB gets the runtime's JSON performance without taking a package dependency. The mapping is
/// straightforward apart from the five CuteDB types JSON has no spelling for — see
/// <see cref="CuteJsonOptions.Extended"/>.
/// </remarks>
public static class CuteJson
{
    private const string DateTag = "$date";
    private const string BinaryTag = "$binary";
    private const string GuidTag = "$guid";
    private const string IdTag = "$id";
    private const string DecimalTag = "$decimal";

    /// <summary>Parses JSON text into a value.</summary>
    public static CuteValue Parse(string json, CuteJsonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json), options);
    }

    /// <summary>Parses UTF-8 JSON into a value.</summary>
    public static CuteValue Parse(ReadOnlySpan<byte> utf8Json, CuteJsonOptions? options = null)
    {
        options ??= CuteJsonOptions.Default;
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        if (!reader.Read())
        {
            throw new CuteDbException("The JSON input is empty.");
        }

        try
        {
            return ReadValue(ref reader, options);
        }
        catch (JsonException ex)
        {
            throw new CuteDbException($"Invalid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Parses a JSON array of objects into documents, streaming rather than buffering.</summary>
    public static IEnumerable<CuteDocument> ParseArray(string json, CuteJsonOptions? options = null)
    {
        var value = Parse(json, options);
        if (!value.IsArray)
        {
            throw new CuteDbException($"Expected a JSON array of objects, found {value.Type.ToDisplayName()}.");
        }

        var documents = new List<CuteDocument>(value.Count);
        foreach (var item in value.AsArray.AsSpan())
        {
            if (!item.IsObject)
            {
                throw new CuteDbException($"Every element must be an object, found {item.Type.ToDisplayName()}.");
            }

            documents.Add(new CuteDocument(item.AsObject));
        }

        return documents;
    }

    /// <summary>Renders a value as JSON text.</summary>
    public static string Write(CuteValue value, bool indented = false)
        => Write(value, indented ? new CuteJsonOptions { Indented = true } : CuteJsonOptions.Default);

    /// <summary>Renders a value as JSON text using explicit options.</summary>
    public static string Write(CuteValue value, CuteJsonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = options.Indented,
            SkipValidation = true,

            // The default encoder escapes '+', '<' and friends, which turns readable exports into
            // a wall of \u escapes. Relaxed still escapes everything that would break the JSON.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteValue(writer, value, options);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Renders a value as UTF-8 JSON into a buffer.</summary>
    public static void Write(IBufferWriter<byte> buffer, CuteValue value, CuteJsonOptions options)
    {
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = options.Indented,
            SkipValidation = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        WriteValue(writer, value, options);
    }

    private static CuteValue ReadValue(ref Utf8JsonReader reader, CuteJsonOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return CuteValue.Null;

            case JsonTokenType.True:
                return CuteValue.Boolean(true);

            case JsonTokenType.False:
                return CuteValue.Boolean(false);

            case JsonTokenType.String:
                return CuteValue.String(reader.GetString()!);

            case JsonTokenType.Number:
                return ReadNumber(ref reader, options);

            case JsonTokenType.StartArray:
            {
                var array = new CuteArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    array.Add(ReadValue(ref reader, options));
                }

                return CuteValue.Array(array);
            }

            case JsonTokenType.StartObject:
                return ReadObject(ref reader, options);

            default:
                throw new CuteDbException($"Unexpected JSON token {reader.TokenType}.");
        }
    }

    private static CuteValue ReadObject(ref Utf8JsonReader reader, CuteJsonOptions options)
    {
        var obj = new CuteObject();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            if (!reader.Read())
            {
                throw new CuteDbException($"Field '{key}' has no value.");
            }

            obj.Set(key, ReadValue(ref reader, options));
        }

        // A one-field object whose key starts with '$' may be one of the tagged forms that carry a
        // type JSON cannot spell. Anything that does not convert is left as the plain object it
        // already is, so a document with a real field called "$date" survives untouched.
        if (obj.Count == 1 && TryUntag(obj, out var tagged))
        {
            return tagged;
        }

        return CuteValue.Object(obj);
    }

    private static bool TryUntag(CuteObject obj, out CuteValue value)
    {
        var (key, raw) = obj.GetAt(0);
        value = CuteValue.Missing;
        if (key.Length == 0 || key[0] != '$' || !raw.TryGetString(out var text))
        {
            return false;
        }

        switch (key)
        {
            case DateTag when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date):
                value = CuteValue.DateTime(date);
                return true;

            case BinaryTag:
                try
                {
                    value = CuteValue.Binary(Convert.FromBase64String(text));
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }

            case GuidTag when Guid.TryParse(text, out var guid):
                value = CuteValue.Guid(guid);
                return true;

            case IdTag when CuteId.TryParse(text, out var id):
                value = CuteValue.Id(id);
                return true;

            case DecimalTag when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number):
                value = CuteValue.Decimal(number);
                return true;

            default:
                return false;
        }
    }

    private static CuteValue ReadNumber(ref Utf8JsonReader reader, CuteJsonOptions options)
    {
        // Integers keep the narrowest type that holds them, which keeps encoded documents small:
        // most numbers in real data are small integers, and Int32 costs four bytes to Int64's
        // eight.
        if (reader.TryGetInt32(out var i32))
        {
            return CuteValue.Int32(i32);
        }

        if (reader.TryGetInt64(out var i64))
        {
            return CuteValue.Int64(i64);
        }

        if (options.PreferDecimal && reader.TryGetDecimal(out var dec))
        {
            return CuteValue.Decimal(dec);
        }

        return CuteValue.Double(reader.GetDouble());
    }

    private static void WriteValue(Utf8JsonWriter writer, CuteValue value, CuteJsonOptions options)
    {
        switch (value.Type)
        {
            case CuteType.Missing:
            case CuteType.Null:
                writer.WriteNullValue();
                break;

            case CuteType.True:
                writer.WriteBooleanValue(true);
                break;

            case CuteType.False:
                writer.WriteBooleanValue(false);
                break;

            case CuteType.Int32:
                writer.WriteNumberValue(value.AsInt32);
                break;

            case CuteType.Int64:
                writer.WriteNumberValue(value.AsInt64);
                break;

            case CuteType.Double:
            {
                var d = value.AsDouble;

                // JSON has no way to spell these three, and emitting a bare NaN produces invalid
                // JSON that no client can read back. Null is the least surprising stand-in.
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteNumberValue(d);
                }

                break;
            }

            case CuteType.Decimal:
                if (options.Extended)
                {
                    WriteTagged(writer, DecimalTag, value.AsDecimal.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.WriteNumberValue(value.AsDecimal);
                }

                break;

            case CuteType.String:
                writer.WriteStringValue(value.AsString);
                break;

            case CuteType.Binary:
                if (options.Extended)
                {
                    WriteTagged(writer, BinaryTag, Convert.ToBase64String(value.AsBinary));
                }
                else
                {
                    writer.WriteBase64StringValue(value.AsBinary);
                }

                break;

            case CuteType.DateTime:
                if (options.Extended)
                {
                    WriteTagged(writer, DateTag, value.AsDateTime.ToString("O", CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.WriteStringValue(value.AsDateTime);
                }

                break;

            case CuteType.Guid:
                if (options.Extended)
                {
                    WriteTagged(writer, GuidTag, value.AsGuid.ToString());
                }
                else
                {
                    writer.WriteStringValue(value.AsGuid);
                }

                break;

            case CuteType.Id:
                if (options.Extended)
                {
                    WriteTagged(writer, IdTag, value.AsId.ToString());
                }
                else
                {
                    writer.WriteStringValue(value.AsId.ToString());
                }

                break;

            case CuteType.Array:
                writer.WriteStartArray();
                foreach (var item in value.AsArray.AsSpan())
                {
                    WriteValue(writer, item, options);
                }

                writer.WriteEndArray();
                break;

            case CuteType.Object:
                writer.WriteStartObject();
                foreach (var (key, field) in value.AsObject)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, field, options);
                }

                writer.WriteEndObject();
                break;

            default:
                throw new CuteDbException($"Cannot write value type {(byte)value.Type} as JSON.");
        }
    }

    private static void WriteTagged(Utf8JsonWriter writer, string tag, string text)
    {
        writer.WriteStartObject();
        writer.WriteString(tag, text);
        writer.WriteEndObject();
    }
}
