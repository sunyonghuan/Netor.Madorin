using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站流量限制相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteLimitTools(BtApiClient client)
{
    /// <summary>
    /// 获取、设置或关闭流量限制。
    /// action 支持 get、set、close。
    /// </summary>
    [Tool(Name = "bt_set_site_limit", Description = "获取、设置或关闭网站流量限制。")]
    public async Task<string> SetSiteLimit(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "动作：get、set、close")] string action,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "并发限制，action=set 时必填")] string perserver,
        [Parameter(Description = "单 IP 限制，action=set 时必填")] string perip,
        [Parameter(Description = "流量限制，action=set 时必填")] string limitRate)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(action) || BtToolSupport.IsBlank(id))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、action、id 都是必填项。", null);

        var normalizedAction = action.ToLowerInvariant();
        string relativePath;
        Dictionary<string, string?> fields;

        if (normalizedAction == "get")
        {
            relativePath = "/site?action=GetLimitNet";
            fields = new Dictionary<string, string?> { ["id"] = id };
        }
        else if (normalizedAction == "set")
        {
            if (BtToolSupport.IsBlank(perserver) || BtToolSupport.IsBlank(perip) || BtToolSupport.IsBlank(limitRate))
                return BtToolSupport.Failure("INVALID_ARGUMENT", "action=set 时 perserver、perip、limitRate 必填。", null);
            relativePath = "/site?action=SetLimitNet";
            fields = new Dictionary<string, string?>
            {
                ["id"] = id,
                ["perserver"] = perserver,
                ["perip"] = perip,
                ["limit_rate"] = limitRate
            };
        }
        else if (normalizedAction == "close")
        {
            relativePath = "/site?action=CloseLimitNet";
            fields = new Dictionary<string, string?> { ["id"] = id };
        }
        else
        {
            return BtToolSupport.Failure("INVALID_ARGUMENT", "action 仅支持 get、set、close。", null);
        }

        var result = await client.PostFormAsync(panelUrl, apiSk, relativePath, fields);
        return result.Success
            ? BtToolSupport.Success("网站流量限制操作已完成。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "网站流量限制操作失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
