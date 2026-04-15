using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站密码访问、防跨站和日志开关相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteSecurityTools(BtApiClient client)
{
    /// <summary>
    /// 统一管理站点安全相关设置。
    /// action 支持 toggle_userini、set_pwd、close_pwd、toggle_logs。
    /// </summary>
    [Tool(Name = "bt_set_site_security", Description = "设置防跨站、密码访问或访问日志开关。")]
    public async Task<string> SetSiteSecurity(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "动作：toggle_userini、set_pwd、close_pwd、toggle_logs")] string action,
        [Parameter(Description = "网站 ID。toggle_logs、set_pwd、close_pwd 时必填")] string id,
        [Parameter(Description = "网站根目录。toggle_userini 时必填")] string path,
        [Parameter(Description = "用户名，action=set_pwd 时必填")] string username,
        [Parameter(Description = "密码，action=set_pwd 时必填")] string password)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(action))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、action 都是必填项。", null);

        var normalizedAction = action.ToLowerInvariant();
        string relativePath;
        Dictionary<string, string?> fields;

        switch (normalizedAction)
        {
            case "toggle_userini":
                if (BtToolSupport.IsBlank(path))
                    return BtToolSupport.Failure("INVALID_ARGUMENT", "action=toggle_userini 时 path 必填。", null);
                relativePath = "/site?action=SetDirUserINI";
                fields = new Dictionary<string, string?> { ["path"] = path };
                break;

            case "set_pwd":
                if (BtToolSupport.IsBlank(id) || BtToolSupport.IsBlank(username) || BtToolSupport.IsBlank(password))
                    return BtToolSupport.Failure("INVALID_ARGUMENT", "action=set_pwd 时 id、username、password 必填。", null);
                relativePath = "/site?action=SetHasPwd";
                fields = new Dictionary<string, string?>
                {
                    ["id"] = id,
                    ["username"] = username,
                    ["password"] = password
                };
                break;

            case "close_pwd":
                if (BtToolSupport.IsBlank(id))
                    return BtToolSupport.Failure("INVALID_ARGUMENT", "action=close_pwd 时 id 必填。", null);
                relativePath = "/site?action=CloseHasPwd";
                fields = new Dictionary<string, string?> { ["id"] = id };
                break;

            case "toggle_logs":
                if (BtToolSupport.IsBlank(id))
                    return BtToolSupport.Failure("INVALID_ARGUMENT", "action=toggle_logs 时 id 必填。", null);
                relativePath = "/site?action=logsOpen";
                fields = new Dictionary<string, string?> { ["id"] = id };
                break;

            default:
                return BtToolSupport.Failure("INVALID_ARGUMENT", "action 仅支持 toggle_userini、set_pwd、close_pwd、toggle_logs。", null);
        }

        var result = await client.PostFormAsync(panelUrl, apiSk, relativePath, fields);
        return result.Success
            ? BtToolSupport.Success("网站安全设置请求已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "网站安全设置失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
