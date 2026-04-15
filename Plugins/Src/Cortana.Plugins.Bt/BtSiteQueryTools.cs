using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站查询相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteQueryTools(BtApiClient client)
{
    [Tool(Name = "bt_list_sites", Description = "分页查询网站列表。")]
    public async Task<string> ListSites(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "每页条数，必须大于 0")] int limit,
        [Parameter(Description = "页码，默认 1")] int page,
        [Parameter(Description = "搜索关键词，空字符串表示不过滤")] string search)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || limit <= 0)
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk 必填，limit 必须大于 0。", null);

        var fields = new Dictionary<string, string?>
        {
            ["limit"] = limit.ToString(),
            ["p"] = page <= 0 ? "1" : page.ToString(),
            ["type"] = "0",
            ["search"] = search
        };

        var result = await client.PostFormAsync(panelUrl, apiSk, "/data?action=getData&table=sites", fields);
        return result.Success
            ? BtToolSupport.Success("已获取网站列表。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "获取网站列表失败。", BtToolSupport.CreateResponseEnvelope(result));
    }

    [Tool(Name = "bt_get_php_versions", Description = "查询已安装 PHP 版本列表。")]
    public async Task<string> GetPhpVersions(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk)
    {
        var result = await client.PostFormAsync(panelUrl, apiSk, "/site?action=GetPHPVersion", null);
        return result.Success
            ? BtToolSupport.Success("已获取 PHP 版本列表。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "获取 PHP 版本列表失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
