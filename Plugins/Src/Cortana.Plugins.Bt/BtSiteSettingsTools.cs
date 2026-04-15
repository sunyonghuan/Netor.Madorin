using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站基础设置相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteSettingsTools(BtApiClient client)
{
    /// <summary>
    /// 设置网站到期时间。
    /// 传入 0000-00-00 表示永久不过期。
    /// </summary>
    [Tool(Name = "bt_set_site_expire", Description = "设置网站到期时间。")]
    public async Task<string> SetSiteExpire(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "到期时间，永久可传 0000-00-00")] string edate)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(id) || BtToolSupport.IsBlank(edate))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、id、edate 都是必填项。", null);

        var fields = new Dictionary<string, string?>
        {
            ["id"] = id,
            ["edate"] = edate
        };

        // 到期时间本身不做格式转换，直接按调用方提供值透传给宝塔接口。
        var result = await client.PostFormAsync(panelUrl, apiSk, "/site?action=SetEdate", fields);
        return result.Success
            ? BtToolSupport.Success("网站到期时间设置请求已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "设置网站到期时间失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
