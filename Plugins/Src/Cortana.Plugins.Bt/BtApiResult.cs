using System.Text.Json.Serialization;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 表示一次宝塔 API 调用的原始结果。
/// 这里保留请求地址、状态码和响应 JSON，方便 AI 和开发者排查问题。
/// </summary>
public sealed record BtApiResult
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("statusCode")] public int StatusCode { get; init; }
    [JsonPropertyName("requestUrl")] public string RequestUrl { get; init; } = string.Empty;
    [JsonPropertyName("responseJson")] public string ResponseJson { get; init; } = string.Empty;
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
}
