namespace Netor.Cortana.AI.Reliability;

/// <summary>
/// 失败分类器，根据异常类型和错误信息将失败分类为不同类型，并返回对应的重试策略。
/// 详见 Docs/已完成功能规划/工作模式方案策划/16-长任务可靠性与重试策略.md §5.2。
/// </summary>
public static class FailureClassifier
{
    /// <summary>
    /// 分类失败并返回失败类型和最大重试次数。
    /// </summary>
    public static (FailureCategory Category, int MaxRetries) Classify(Exception? exception, string? errorMessage)
    {
        if (exception is null && string.IsNullOrWhiteSpace(errorMessage))
            return (FailureCategory.Unknown, 0);

        var exceptionType = exception?.GetType().Name ?? string.Empty;
        var message = errorMessage ?? exception?.Message ?? string.Empty;

        if (IsNetworkError(exceptionType, message))
            return (FailureCategory.Network, 5);

        if (IsToolError(exceptionType, message))
            return (FailureCategory.Tool, 3);

        if (IsModelOutputError(exceptionType, message))
            return (FailureCategory.ModelOutput, 2);

        if (IsUserCancellation(exceptionType, message))
            return (FailureCategory.UserCancellation, 0);

        if (IsConfigurationError(exceptionType, message))
            return (FailureCategory.Configuration, 0);

        return (FailureCategory.Unknown, 0);
    }

    private static bool IsNetworkError(string exceptionType, string message)
    {
        if (exceptionType.Contains("Http", StringComparison.OrdinalIgnoreCase) ||
            exceptionType.Contains("Socket", StringComparison.OrdinalIgnoreCase) ||
            exceptionType.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return true;

        var networkKeywords = new[]
        {
            "timeout", "timed out", "connection", "network", "502", "503", "504",
            "gateway", "unavailable", "unreachable", "refused"
        };

        return ContainsAny(message, networkKeywords);
    }

    private static bool IsToolError(string exceptionType, string message)
    {
        var toolKeywords = new[]
        {
            "tool execution", "tool failed", "function call", "permission denied",
            "file not found", "access denied", "invalid argument"
        };

        return ContainsAny(message, toolKeywords);
    }

    private static bool IsModelOutputError(string exceptionType, string message)
    {
        if (exceptionType.Contains("Json", StringComparison.OrdinalIgnoreCase))
            return true;

        var outputKeywords = new[]
        {
            "json", "parse", "deserialize", "invalid format", "malformed",
            "unexpected token", "schema validation"
        };

        return ContainsAny(message, outputKeywords);
    }

    private static bool IsUserCancellation(string exceptionType, string message)
    {
        if (exceptionType.Contains("OperationCanceled", StringComparison.OrdinalIgnoreCase) ||
            exceptionType.Contains("TaskCanceled", StringComparison.OrdinalIgnoreCase))
            return true;

        return message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("abort", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfigurationError(string exceptionType, string message)
    {
        var configKeywords = new[]
        {
            "configuration", "api key", "authentication", "unauthorized", "401",
            "forbidden", "403", "invalid credentials", "missing configuration"
        };

        return ContainsAny(message, configKeywords);
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// 失败类型分类。
/// </summary>
public enum FailureCategory
{
    /// <summary>网络错误（超时、连接失败、502/503/504 等）。</summary>
    Network,

    /// <summary>工具执行错误（权限、文件不存在、参数错误等）。</summary>
    Tool,

    /// <summary>模型输出格式错误（JSON 解析失败、schema 不匹配等）。</summary>
    ModelOutput,

    /// <summary>用户取消操作。</summary>
    UserCancellation,

    /// <summary>配置错误（API Key 错误、认证失败等）。</summary>
    Configuration,

    /// <summary>未知错误。</summary>
    Unknown
}
