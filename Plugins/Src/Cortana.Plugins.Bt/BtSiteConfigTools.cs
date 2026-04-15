using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站配置文件读取和保存相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteConfigTools(BtApiClient client)
{
    /// <summary>
    /// 读取或保存站点配置文件。
    /// get 返回文件内容，set 直接覆盖指定路径的文本内容。
    /// </summary>
    [Tool(Name = "bt_get_or_set_site_config", Description = "读取或保存网站配置文件内容。")]
    public async Task<string> GetOrSetSiteConfig(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "动作：get 或 set")] string action,
        [Parameter(Description = "配置文件路径")] string path,
        [Parameter(Description = "写入内容，action=set 时必填，其它情况传空字符串")] string data)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(action) || BtToolSupport.IsBlank(path))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、action、path 都是必填项。", null);

        var normalizedAction = action.ToLowerInvariant();
        string relativePath;
        Dictionary<string, string?> fields;

        if (normalizedAction == "get")
        {
            relativePath = "/files?action=GetFileBody";
            fields = new Dictionary<string, string?>
            {
                ["path"] = path
            };
        }
        else if (normalizedAction == "set")
        {
            if (BtToolSupport.IsBlank(data))
                return BtToolSupport.Failure("INVALID_ARGUMENT", "action=set 时 data 必填。", null);

            // 宝塔文档要求保存文件时显式传递 encoding=utf-8。
            relativePath = "/files?action=SaveFileBody";
            fields = new Dictionary<string, string?>
            {
                ["path"] = path,
                ["data"] = data,
                ["encoding"] = "utf-8"
            };
        }
        else
        {
            return BtToolSupport.Failure("INVALID_ARGUMENT", "action 仅支持 get 或 set。", null);
        }

        var result = await client.PostFormAsync(panelUrl, apiSk, relativePath, fields);
        return result.Success
            ? BtToolSupport.Success("网站配置文件操作已完成。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "网站配置文件操作失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
