using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站创建、删除、启停相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteManageTools(BtApiClient client)
{
    /// <summary>
    /// 创建一个新的宝塔网站。
    /// 当前实现按 PHP 站点创建，默认不开启 FTP 和数据库。
    /// </summary>
    [Tool(Name = "bt_add_site", Description = "创建宝塔网站。")]
    public async Task<string> AddSite(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "主域名，例如 demo.example.com")] string domain,
        [Parameter(Description = "网站根目录，例如 /www/wwwroot/demo")] string path,
        [Parameter(Description = "PHP 版本，例如 80，纯静态可传 00")] string version,
        [Parameter(Description = "网站备注")] string ps)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(domain) || BtToolSupport.IsBlank(path) || BtToolSupport.IsBlank(version))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、domain、path、version 都是必填项。", null);

        // 宝塔要求 webname 字段为 JSON 字符串，而不是普通表单字段。
        var webname = $"{{\"domain\":\"{domain}\",\"domainlist\":[],\"count\":0}}";
        var fields = new Dictionary<string, string?>
        {
            ["webname"] = webname,
            ["path"] = path,
            ["type_id"] = "0",
            ["type"] = "PHP",
            ["version"] = version,
            ["port"] = "80",
            ["ps"] = ps,
            ["ftp"] = "false",
            ["sql"] = "false"
        };

        // 创建站点本质上是一次标准表单提交，最终是否成功以宝塔返回内容为准。
        var result = await client.PostFormAsync(panelUrl, apiSk, "/site?action=AddSite", fields);
        return result.Success
            ? BtToolSupport.Success("网站创建请求已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "创建网站失败。", BtToolSupport.CreateResponseEnvelope(result));
    }

    /// <summary>
    /// 删除一个宝塔网站。
    /// 可选同时删除站点目录，但不会自动删除数据库和 FTP。
    /// </summary>
    [Tool(Name = "bt_delete_site", Description = "删除宝塔网站。")]
    public async Task<string> DeleteSite(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "网站名称")] string webname,
        [Parameter(Description = "是否删除网站目录，true 或 false")] string deletePath)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(id) || BtToolSupport.IsBlank(webname))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、id、webname 都是必填项。", null);

        var fields = new Dictionary<string, string?>
        {
            ["id"] = id,
            ["webname"] = webname,
            ["path"] = deletePath.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : null
        };

        // 当 deletePath=true 时，向宝塔传递 path=1 以删除网站根目录。
        var result = await client.PostFormAsync(panelUrl, apiSk, "/site?action=DeleteSite", fields);
        return result.Success
            ? BtToolSupport.Success("网站删除请求已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "删除网站失败。", BtToolSupport.CreateResponseEnvelope(result));
    }

    /// <summary>
    /// 启用或停用指定网站。
    /// targetStatus 只允许 start 或 stop。
    /// </summary>
    [Tool(Name = "bt_set_site_status", Description = "启用或停用网站。")]
    public async Task<string> SetSiteStatus(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "网站主域名")] string name,
        [Parameter(Description = "目标状态：start 或 stop")] string targetStatus)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(id) || BtToolSupport.IsBlank(name))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、id、name 都是必填项。", null);

        // 工具层对 start/stop 做一次映射，避免调用方直接关心宝塔内部 action 名称。
        var action = string.Equals(targetStatus, "start", StringComparison.OrdinalIgnoreCase)
            ? "SiteStart"
            : string.Equals(targetStatus, "stop", StringComparison.OrdinalIgnoreCase)
                ? "SiteStop"
                : string.Empty;
        if (string.IsNullOrEmpty(action))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "targetStatus 仅支持 start 或 stop。", null);

        var fields = new Dictionary<string, string?>
        {
            ["id"] = id,
            ["name"] = name
        };

        var result = await client.PostFormAsync(panelUrl, apiSk, $"/site?action={action}", fields);
        return result.Success
            ? BtToolSupport.Success("网站状态变更请求已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "网站状态变更失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
