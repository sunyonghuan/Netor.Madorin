using System.Text.Json;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 工具层公共辅助方法。
/// 这里集中处理统一返回和基础参数校验，避免工具类重复代码。
/// </summary>
internal static class BtToolSupport
{
    public static string Success(string message, string responseJson) => ToolResult.Ok(message, responseJson);

    public static string Failure(string code, string message, string? details = null) => ToolResult.Fail(code, message, details);

    public static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    public static bool TryParsePanelUrl(string panelUrl, out Uri? uri)
    {
        return Uri.TryCreate(panelUrl, UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public static string CreateResponseEnvelope(BtApiResult result)
    {
        return JsonSerializer.Serialize(result, PluginJsonContext.Default.BtApiResult);
    }
}
