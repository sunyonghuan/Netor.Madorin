using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站域名增删相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteDomainTools(BtApiClient client)
{
    /// <summary>
    /// 为网站添加或删除域名。
    /// 添加和删除共用同一个工具，通过 action 区分具体行为。
    /// </summary>
    [Tool(Name = "bt_manage_site_domain", Description = "添加或删除网站域名。")]
    public async Task<string> ManageSiteDomain(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "动作：add 或 delete")] string action,
        [Parameter(Description = "网站 ID")] string id,
        [Parameter(Description = "网站名称")] string webname,
        [Parameter(Description = "域名")] string domain,
        [Parameter(Description = "端口，删除时需要，默认 80")] string port)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(action) || BtToolSupport.IsBlank(id) || BtToolSupport.IsBlank(webname) || BtToolSupport.IsBlank(domain))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、action、id、webname、domain 都是必填项。", null);

        var normalizedAction = action.ToLowerInvariant();
        string relativePath;
        Dictionary<string, string?> fields;

        if (normalizedAction == "add")
        {
            relativePath = "/site?action=AddDomain";
            fields = new Dictionary<string, string?>
            {
                ["id"] = id,
                ["webname"] = webname,
                ["domain"] = domain
            };
        }
        else if (normalizedAction == "delete")
        {
            // 删除域名时宝塔要求显式提供端口，未传时按 80 处理。
            relativePath = "/site?action=DelDomain";
            fields = new Dictionary<string, string?>
            {
                ["id"] = id,
                ["webname"] = webname,
                ["domain"] = domain,
                ["port"] = string.IsNullOrWhiteSpace(port) ? "80" : port
            };
        }
        else
        {
            return BtToolSupport.Failure("INVALID_ARGUMENT", "action 仅支持 add 或 delete。", null);
        }

        var result = await client.PostFormAsync(panelUrl, apiSk, relativePath, fields);
        return result.Success
            ? BtToolSupport.Success("网站域名操作已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "网站域名操作失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
