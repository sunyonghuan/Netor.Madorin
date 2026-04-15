using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站备注修改相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteNoteTools(BtApiClient client)
{
    /// <summary>
    /// 修改网站备注。
    /// 该工具只更新备注文本，不影响站点其它配置。
    /// </summary>
    [Tool(Name = "bt_set_site_note", Description = "修改网站备注。")]
    public async Task<string> SetSiteNote(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "新的备注内容")] string ps)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(id) || ps is null)
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、id 都是必填项，ps 不能为 null。", null);

        var fields = new Dictionary<string, string?>
        {
            ["id"] = id,
            ["ps"] = ps
        };

        var result = await client.PostFormAsync(panelUrl, apiSk, "/data?action=setPs&table=sites", fields);
        return result.Success
            ? BtToolSupport.Success("网站备注修改请求已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "修改网站备注失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
