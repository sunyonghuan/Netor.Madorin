namespace Cortana.Plugins.Office;

/// <summary>
/// 插件统一错误码常量，与接口契约保持一致。
/// </summary>
internal static class ErrorCodes
{
    public const string Ok = "OK";
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string UnsupportedFormat = "UNSUPPORTED_FORMAT";
    public const string PathForbidden = "PATH_FORBIDDEN";
    public const string ConflictExists = "CONFLICT_EXISTS";
    public const string ContentNotFound = "CONTENT_NOT_FOUND";
    public const string InternalError = "INTERNAL_ERROR";
}
