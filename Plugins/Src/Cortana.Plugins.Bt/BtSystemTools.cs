using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与系统状态相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSystemTools(BtApiClient client)
{
    [Tool(Name = "bt_get_system_total", Description = "查询宝塔面板服务器系统总体状态。")]
    public async Task<string> GetSystemTotal(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk)
    {
        return await CallSimpleAsync(panelUrl, apiSk, "/system?action=GetSystemTotal", "已获取系统总体状态。");
    }

    [Tool(Name = "bt_get_disk_info", Description = "查询宝塔面板服务器磁盘分区信息。")]
    public async Task<string> GetDiskInfo(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk)
    {
        return await CallSimpleAsync(panelUrl, apiSk, "/system?action=GetDiskInfo", "已获取磁盘分区信息。");
    }

    [Tool(Name = "bt_get_network_status", Description = "查询 CPU、内存、网络与负载状态。")]
    public async Task<string> GetNetworkStatus(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk)
    {
        return await CallSimpleAsync(panelUrl, apiSk, "/system?action=GetNetWork", "已获取网络与负载状态。");
    }

    /// <summary>
    /// 统一处理简单查询型接口，减少重复的参数校验和结果包装。
    /// </summary>
    private async Task<string> CallSimpleAsync(string panelUrl, string apiSk, string relativePath, string successMessage)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl 与 apiSk 都是必填项。", null);

        var result = await client.PostFormAsync(panelUrl, apiSk, relativePath, null);
        return result.Success
            ? BtToolSupport.Success(successMessage, BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "宝塔 API 请求失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
