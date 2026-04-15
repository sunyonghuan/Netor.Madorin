using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站域名、备份等资源列表查询相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteResourceQueryTools(BtApiClient client)
{
    /// <summary>
    /// 查询指定网站的域名列表。
    /// </summary>
    [Tool(Name = "bt_get_site_domains", Description = "查询指定网站的域名列表。")]
    public async Task<string> GetSiteDomains(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "网站 ID")] string siteId)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(siteId))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、siteId 都是必填项。", null);

        var fields = new Dictionary<string, string?>
        {
            ["search"] = siteId,
            ["list"] = "true"
        };

        var result = await client.PostFormAsync(panelUrl, apiSk, "/data?action=getData&table=domain", fields);
        return result.Success
            ? BtToolSupport.Success("已获取网站域名列表。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "获取网站域名列表失败。", BtToolSupport.CreateResponseEnvelope(result));
    }

    /// <summary>
    /// 查询指定网站的备份列表。
    /// </summary>
    [Tool(Name = "bt_get_site_backups", Description = "查询指定网站的备份列表。")]
    public async Task<string> GetSiteBackups(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "网站 ID")] string siteId,
        [Parameter(Description = "每页条数，默认 10")] int limit,
        [Parameter(Description = "页码，默认 1")] int page)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(siteId))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、siteId 都是必填项。", null);

        var fields = new Dictionary<string, string?>
        {
            ["p"] = page <= 0 ? "1" : page.ToString(),
            ["limit"] = limit <= 0 ? "10" : limit.ToString(),
            ["type"] = "0",
            ["search"] = siteId
        };

        var result = await client.PostFormAsync(panelUrl, apiSk, "/data?action=getData&table=backup", fields);
        return result.Success
            ? BtToolSupport.Success("已获取网站备份列表。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "获取网站备份列表失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
