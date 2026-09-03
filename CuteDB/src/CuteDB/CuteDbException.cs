namespace CuteDB;

/// <summary>
/// The base exception for every error CuteDB raises on purpose.
/// </summary>
/// <remarks>
/// Catching this one type is enough to catch anything CuteDB reports as a usage or data problem.
/// I/O failures from the underlying file system still surface as <see cref="IOException"/> and are
/// deliberately not wrapped, because the caller can act on those differently.
/// </remarks>
public class CuteDbException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public CuteDbException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    public CuteDbException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a stored file is not a CuteDB database, or is damaged beyond recovery.</summary>
public sealed class CuteCorruptionException : CuteDbException
{
    /// <summary>Creates the exception with a message.</summary>
    public CuteCorruptionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    public CuteCorruptionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Raised when CuteQL text cannot be parsed, carrying the offset so callers can point at it.
/// </summary>
public sealed class CuteQueryException : CuteDbException
{
    /// <summary>Creates the exception for a failure at a known offset in the query text.</summary>
    public CuteQueryException(string message, string query, int position)
        : base(Describe(message, query, position))
    {
        Query = query;
        Position = position;
    }

    /// <summary>The query text that failed.</summary>
    public string Query { get; }

    /// <summary>The zero-based character offset the failure was detected at.</summary>
    public int Position { get; }

    private static string Describe(string message, string query, int position)
    {
        if (position < 0 || position > query.Length)
        {
            return message;
        }

        // A caret under the offending character reads far better in a terminal than "at index 47".
        var lineStart = query.LastIndexOf('\n', Math.Max(0, Math.Min(position, query.Length - 1))) + 1;
        var lineEnd = query.IndexOf('\n', lineStart);
        if (lineEnd < 0)
        {
            lineEnd = query.Length;
        }

        var line = query[lineStart..lineEnd];
        var caret = new string(' ', position - lineStart) + "^";
        return $"{message}{Environment.NewLine}  {line}{Environment.NewLine}  {caret}";
    }
}
