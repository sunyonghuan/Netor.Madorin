namespace Cortana.Plugins.GoogleSearch;

/// <summary>
/// 插件错误码常量。
/// </summary>
public static class ErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string AuthFailed = "AUTH_FAILED";
    public const string QuotaExceeded = "QUOTA_EXCEEDED";
    public const string NetworkError = "NETWORK_ERROR";
    public const string Timeout = "TIMEOUT";
    public const string ConfigNotInitialized = "CONFIG_NOT_INITIALIZED";
    public const string UpstreamError = "UPSTREAM_ERROR";
    public const string InternalError = "INTERNAL_ERROR";
}