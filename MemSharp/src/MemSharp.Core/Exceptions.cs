namespace MemSharp;

/// <summary>Base class for every error the engine raises as part of normal operation.</summary>
/// <remarks>
/// These map onto RESP error replies. <see cref="Code"/> is the leading token the server sends -
/// clients switch on it, so it is part of the wire contract.
/// </remarks>
public abstract class MemSharpException : Exception
{
    /// <summary>Initialises the exception.</summary>
    protected MemSharpException(string message, Exception? inner = null) : base(message, inner) { }

    /// <summary>The RESP error code, e.g. <c>WRONGTYPE</c>.</summary>
    public abstract string Code { get; }
}

/// <summary>An operation was applied to a key holding a different <see cref="MemType"/>.</summary>
public sealed class WrongTypeException : MemSharpException
{
    /// <summary>Initialises the exception for a key of the wrong kind.</summary>
    public WrongTypeException(string key, MemType actual, MemType expected)
        : base($"Operation against a key holding the wrong kind of value (key '{key}' is {actual}, expected {expected})")
    {
        Key = key;
        Actual = actual;
        Expected = expected;
    }

    /// <summary>The offending key.</summary>
    public string Key { get; }

    /// <summary>What the key actually holds.</summary>
    public MemType Actual { get; }

    /// <summary>What the operation required.</summary>
    public MemType Expected { get; }

    /// <inheritdoc />
    public override string Code => "WRONGTYPE";
}

/// <summary>A string value was not parseable as the number an operation required.</summary>
public sealed class NotANumberException : MemSharpException
{
    /// <summary>Initialises the exception.</summary>
    public NotANumberException(string message) : base(message) { }

    /// <inheritdoc />
    public override string Code => "ERR";
}

/// <summary>A command was syntactically valid but semantically rejected.</summary>
public sealed class MemSharpCommandException : MemSharpException
{
    /// <summary>Initialises the exception with a RESP error code.</summary>
    public MemSharpCommandException(string message, string code = "ERR") : base(message) => Code = code;

    /// <inheritdoc />
    public override string Code { get; }
}

/// <summary>A persistence file was truncated, corrupt, or written by an incompatible version.</summary>
public sealed class PersistenceException : MemSharpException
{
    /// <summary>Initialises the exception.</summary>
    public PersistenceException(string message, Exception? inner = null) : base(message, inner) { }

    /// <inheritdoc />
    public override string Code => "ERR";
}
