using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站运行目录、根目录和安全状态查询相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteRuntimeTools(BtApiClient client)
{
    /// <summary>
    /// 查询防跨站、日志开关、密码访问与当前运行目录状态。
    /// </summary>
    [Tool(Name = "bt_get_site_security_state", Description = "查询网站防跨站、日志、密码访问和运行目录状态。")]
    public async Task<string> GetSiteSecurityState(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "网站根目录")] string path)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(id) || BtToolSupport.IsBlank(path))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、id、path 都是必填项。", null);

        var fields = new Dictionary<string, string?>
        {
            ["id"] = id,
            ["path"] = path
        };

        var result = await client.PostFormAsync(panelUrl, apiSk, "/site?action=GetDirUserINI", fields);
        return result.Success
            ? BtToolSupport.Success("已获取网站安全状态。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "获取网站安全状态失败。", BtToolSupport.CreateResponseEnvelope(result));
    }

    /// <summary>
    /// 修改网站根目录。
    /// </summary>
    [Tool(Name = "bt_set_site_path", Description = "修改网站根目录。")]
    public async Task<string> SetSitePath(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "新的网站根目录")] string path)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(id) || BtToolSupport.IsBlank(path))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、id、path 都是必填项。", null);

        var fields = new Dictionary<string, string?>
        {
            ["id"] = id,
            ["path"] = path
        };

        var result = await client.PostFormAsync(panelUrl, apiSk, "/site?action=SetPath", fields);
        return result.Success
            ? BtToolSupport.Success("网站根目录修改请求已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "修改网站根目录失败。", BtToolSupport.CreateResponseEnvelope(result));
    }

    /// <summary>
    /// 设置网站运行目录。
    /// runPath 按网站根目录的相对路径传递，例如 /public。
    /// </summary>
    [Tool(Name = "bt_set_site_run_path", Description = "设置网站运行目录。")]
    public async Task<string> SetSiteRunPath(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "运行目录，例如 / 或 /public")] string runPath)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(id) || BtToolSupport.IsBlank(runPath))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、id、runPath 都是必填项。", null);

        var fields = new Dictionary<string, string?>
        {
            ["id"] = id,
            ["runPath"] = runPath
        };

        var result = await client.PostFormAsync(panelUrl, apiSk, "/site?action=SetSiteRunPath", fields);
        return result.Success
            ? BtToolSupport.Success("网站运行目录设置请求已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "设置网站运行目录失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
