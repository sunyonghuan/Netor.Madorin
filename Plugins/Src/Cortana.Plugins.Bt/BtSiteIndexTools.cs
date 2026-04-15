using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站默认文档相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteIndexTools(BtApiClient client)
{
    /// <summary>
    /// 获取或设置默认文档。
    /// action=get 时读取当前配置，action=set 时覆盖默认文档列表。
    /// </summary>
    [Tool(Name = "bt_get_or_set_site_index", Description = "读取或设置网站默认文档。")]
    public async Task<string> GetOrSetSiteIndex(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "动作：get 或 set")] string action,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "默认文档列表，action=set 时必填，例如 index.php,index.html")] string indexList)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(action) || BtToolSupport.IsBlank(id))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、action、id 都是必填项。", null);

        var normalizedAction = action.ToLowerInvariant();
        string relativePath;
        Dictionary<string, string?> fields;

        if (normalizedAction == "get")
        {
            relativePath = "/site?action=GetIndex";
            fields = new Dictionary<string, string?> { ["id"] = id };
        }
        else if (normalizedAction == "set")
        {
            if (BtToolSupport.IsBlank(indexList))
                return BtToolSupport.Failure("INVALID_ARGUMENT", "action=set 时 indexList 必填。", null);
            relativePath = "/site?action=SetIndex";
            fields = new Dictionary<string, string?>
            {
                ["id"] = id,
                ["Index"] = indexList
            };
        }
        else
        {
            return BtToolSupport.Failure("INVALID_ARGUMENT", "action 仅支持 get 或 set。", null);
        }

        var result = await client.PostFormAsync(panelUrl, apiSk, relativePath, fields);
        return result.Success
            ? BtToolSupport.Success("网站默认文档操作已完成。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "网站默认文档操作失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
