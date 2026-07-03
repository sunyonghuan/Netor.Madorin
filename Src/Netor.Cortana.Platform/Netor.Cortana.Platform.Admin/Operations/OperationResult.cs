namespace Netor.Cortana.Platform.Admin.Operations;

public sealed record OperationResult<T>(
    bool Success,
    string? ErrorCode,
    string? Message,
    T? Data)
{
    public static OperationResult<T> Ok(T data, string? message = null)
        => new(true, null, message, data);

    public static OperationResult<T> Fail(string errorCode, string message)
        => new(false, errorCode, message, default);
}

public static class OperationErrorCodes
{
    public const string NotFound = "NOT_FOUND";
    public const string Forbidden = "FORBIDDEN";
    public const string Validation = "VALIDATION";
    public const string RateLimited = "RATE_LIMITED";
    public const string Protected = "PROTECTED";
    public const string Conflict = "CONFLICT";
    public const string IdempotentReplay = "IDEMPOTENT_REPLAY";
    public const string Internal = "INTERNAL";
}
