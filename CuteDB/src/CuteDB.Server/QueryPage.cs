using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CuteDB.Server;

/// <summary>
/// One page of a <c>POST /v1/query</c> result: where it starts, how long it is, and which query it
/// belongs to.
/// </summary>
/// <remarks>
/// <para>
/// <b>What paging saves, and what it does not.</b> The engine materialises a result in full before
/// the caller sees a row (cursors are an engine item, not a server one), so every page re-runs the
/// statement and slices the rows it wants out of the whole result. That keeps the server free of
/// session state — nothing is held between requests, so there is nothing to expire, leak or lose on
/// restart — at the price of the engine doing the full query once per page. What paging does save
/// is everything after the engine: the response carries one page of rows rather than all of them,
/// so serialising, sending and parsing on the client are bounded by the page size. It does not make
/// a query over a million rows cheaper to run.
/// </para>
/// <para>
/// <b>Offsets, not keysets.</b> A page is an offset into the result. A keyset cursor ("rows after
/// this <c>_id</c>") would survive concurrent writes better, but it only exists for a statement
/// whose order is a unique key, and an arbitrary CuteQL statement — grouped, projected, ordered by
/// a computed value — has none. So a write landing between two page requests can shift rows across
/// the boundary; give the statement an <c>ORDER BY</c> on something stable if that matters.
/// </para>
/// <para>
/// The cursor is opaque to callers but deliberately not secret: it is the offset plus a short
/// fingerprint of the statement and its parameters, base64url-encoded. The fingerprint is there to
/// turn the easy mistake — reusing a cursor with a different query — into a 400 instead of a page
/// of some other result.
/// </para>
/// </remarks>
internal sealed record QueryPage(int Offset, int Limit, string Fingerprint)
{
    /// <summary>The page size used when a caller asks to page but gives no limit.</summary>
    internal const int DefaultLimit = 100;

    /// <summary>Same ceiling as <c>GET …/documents</c>, so one page is never an unbounded response.</summary>
    internal const int MaxLimit = 10_000;

    private const string CursorVersion = "1";

    /// <summary>
    /// Reads <c>limit</c>, <c>offset</c> and <c>cursor</c> from a query request body. Returns
    /// <c>true</c> with a null <paramref name="page"/> when the caller did not ask to page at all,
    /// which keeps the unpaged response byte-for-byte what it has always been.
    /// </summary>
    internal static bool TryRead(
        CuteValue body,
        string query,
        out QueryPage? page,
        [NotNullWhen(false)] out string? errorCode,
        [NotNullWhen(false)] out string? error)
    {
        page = null;
        errorCode = null;
        error = null;

        var limitValue = body["limit"];
        var offsetValue = body["offset"];
        var cursorValue = body["cursor"];

        // An explicit null is treated as absent: a client that serialises an unset option as null
        // should get the same answer as one that leaves it out.
        if (limitValue.IsNullOrMissing && offsetValue.IsNullOrMissing && cursorValue.IsNullOrMissing)
        {
            return true;
        }

        var fingerprint = FingerprintOf(query, body["parameters"]);

        var limit = DefaultLimit;
        if (!limitValue.IsNullOrMissing)
        {
            if (!TryGetInteger(limitValue, out limit) || limit < 1 || limit > MaxLimit)
            {
                (errorCode, error) = ("invalid_body", $"'limit' must be an integer from 1 to {MaxLimit}.");
                return false;
            }
        }

        if (!offsetValue.IsNullOrMissing && !cursorValue.IsNullOrMissing)
        {
            (errorCode, error) = ("invalid_body", "Send either 'offset' or 'cursor', not both.");
            return false;
        }

        var offset = 0;
        if (!offsetValue.IsNullOrMissing)
        {
            if (!TryGetInteger(offsetValue, out offset) || offset < 0)
            {
                (errorCode, error) = ("invalid_body", "'offset' must be a non-negative integer.");
                return false;
            }
        }
        else if (!cursorValue.IsNullOrMissing)
        {
            if (!cursorValue.TryGetString(out var cursor) || !TryDecodeCursor(cursor, out offset, out var cursorPrint))
            {
                (errorCode, error) = ("invalid_cursor", "The cursor is not one this server issued.");
                return false;
            }

            if (!string.Equals(cursorPrint, fingerprint, StringComparison.Ordinal))
            {
                (errorCode, error) = ("invalid_cursor",
                    "The cursor belongs to a different query or different parameters. Send the same 'query' and 'parameters' as the request that returned it.");
                return false;
            }
        }

        page = new QueryPage(offset, limit, fingerprint);
        return true;
    }

    /// <summary>The cursor for the page after this one, or null when <paramref name="total"/> rows end here.</summary>
    internal string? NextCursor(int total)
    {
        // Limit is at most 10,000 and Offset came from an int, so the sum cannot overflow a long.
        var next = (long)Offset + Limit;
        return next >= total ? null : EncodeCursor((int)next, Fingerprint);
    }

    private static bool TryGetInteger(CuteValue value, out int result)
    {
        result = 0;
        if (!value.IsNumber || !value.TryGetDouble(out var number)
            || number != Math.Floor(number) || number < int.MinValue || number > int.MaxValue)
        {
            return false;
        }

        result = (int)number;
        return true;
    }

    /// <summary>
    /// A short hash of the statement and its parameters. Not a security boundary — the caller could
    /// read every page by offset anyway — only a guard against mixing cursors up.
    /// </summary>
    private static string FingerprintOf(string query, CuteValue parameters)
    {
        var parameterText = parameters.IsObject ? CuteJson.Write(parameters) : string.Empty;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(query + "\n" + parameterText));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    private static string EncodeCursor(int offset, string fingerprint)
        => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"{CursorVersion}:{offset.ToString(CultureInfo.InvariantCulture)}:{fingerprint}"));

    private static bool TryDecodeCursor(string cursor, out int offset, out string fingerprint)
    {
        offset = 0;
        fingerprint = string.Empty;

        if (cursor.Length is 0 or > 128)
        {
            return false;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = text.Split(':');
        if (parts.Length != 3 || parts[0] != CursorVersion
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset))
        {
            return false;
        }

        fingerprint = parts[2];
        return true;
    }
}
