namespace Madorin.AI.Runtime.Client;

/// <summary>
/// Base exception type for all Client SDK errors.
/// </summary>
public class RuntimeClientException : Exception
{
    /// <summary>Creates a new instance.</summary>
    public RuntimeClientException(string message) : base(message) { }

    /// <summary>Creates a new instance wrapping an inner error.</summary>
    public RuntimeClientException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>Indicates whether the operation that caused this error is safe to retry.</summary>
    public bool IsRetryable { get; init; }
}

/// <summary>Raised when authentication with the Runtime fails.</summary>
public sealed class RuntimeClientAuthenticationException : RuntimeClientException
{
    /// <summary>Creates a new instance.</summary>
    public RuntimeClientAuthenticationException(string message) : base(message) { }

    /// <summary>Creates a new instance wrapping an inner error.</summary>
    public RuntimeClientAuthenticationException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Raised when the Runtime process cannot be started or exits prematurely.</summary>
public sealed class RuntimeClientStartupException : RuntimeClientException
{
    /// <summary>Creates a new instance.</summary>
    public RuntimeClientStartupException(string message) : base(message) { }

    /// <summary>Creates a new instance wrapping an inner error.</summary>
    public RuntimeClientStartupException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Raised when the named-pipe connection to the Runtime fails.</summary>
public sealed class RuntimeClientConnectionException : RuntimeClientException
{
    /// <summary>Creates a new instance.</summary>
    public RuntimeClientConnectionException(string message) : base(message) { }

    /// <summary>Creates a new instance wrapping an inner error.</summary>
    public RuntimeClientConnectionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Raised when the handshake or protocol negotiation fails.</summary>
public sealed class RuntimeClientProtocolException : RuntimeClientException
{
    /// <summary>Creates a new instance.</summary>
    public RuntimeClientProtocolException(string message) : base(message) { }

    /// <summary>Creates a new instance wrapping an inner error.</summary>
    public RuntimeClientProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Raised when the Runtime <c>ProgramVersion</c> does not match the expected value.</summary>
public sealed class RuntimeClientVersionIncompatibleException : RuntimeClientException
{
    /// <summary>The version the client expected.</summary>
    public string? ExpectedVersion { get; init; }

    /// <summary>The version the Runtime actually reported.</summary>
    public string? ActualVersion { get; init; }

    /// <summary>Creates a new instance.</summary>
    public RuntimeClientVersionIncompatibleException(string message) : base(message) { }

    /// <summary>Creates a new instance wrapping an inner error.</summary>
    public RuntimeClientVersionIncompatibleException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Raised when the Runtime fails health checks or initialisation.</summary>
public sealed class RuntimeClientHealthException : RuntimeClientException
{
    /// <summary>Creates a new instance.</summary>
    public RuntimeClientHealthException(string message) : base(message) { }

    /// <summary>Creates a new instance wrapping an inner error.</summary>
    public RuntimeClientHealthException(string message, Exception innerException) : base(message, innerException) { }
}
