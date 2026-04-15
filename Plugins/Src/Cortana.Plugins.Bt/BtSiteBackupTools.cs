using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 与网站备份相关的宝塔工具。
/// </summary>
[Tool]
public sealed class BtSiteBackupTools(BtApiClient client)
{
    /// <summary>
    /// 创建或删除网站备份。
    /// create 需要网站 ID，delete 需要备份 ID。
    /// </summary>
    [Tool(Name = "bt_site_backup", Description = "创建或删除网站备份。")]
    public async Task<string> SiteBackup(
        [Parameter(Description = "宝塔面板地址，例如 http://127.0.0.1:8888")] string panelUrl,
        [Parameter(Description = "宝塔 API 密钥 apiSk")] string apiSk,
        [Parameter(Description = "动作：create 或 delete")] string action,
        [Parameter(Description = "网站 ID，create 时必填")] string siteId,
        [Parameter(Description = "备份 ID，delete 时必填")] string backupId)
    {
        if (BtToolSupport.IsBlank(panelUrl) || BtToolSupport.IsBlank(apiSk) || BtToolSupport.IsBlank(action))
            return BtToolSupport.Failure("INVALID_ARGUMENT", "panelUrl、apiSk、action 都是必填项。", null);

        var normalizedAction = action.ToLowerInvariant();
        string relativePath;
        Dictionary<string, string?> fields;

        if (normalizedAction == "create")
        {
            if (BtToolSupport.IsBlank(siteId))
                return BtToolSupport.Failure("INVALID_ARGUMENT", "action=create 时 siteId 必填。", null);

            relativePath = "/site?action=ToBackup";
            fields = new Dictionary<string, string?> { ["id"] = siteId };
        }
        else if (normalizedAction == "delete")
        {
            if (BtToolSupport.IsBlank(backupId))
                return BtToolSupport.Failure("INVALID_ARGUMENT", "action=delete 时 backupId 必填。", null);

            relativePath = "/site?action=DelBackup";
            fields = new Dictionary<string, string?> { ["id"] = backupId };
        }
        else
        {
            return BtToolSupport.Failure("INVALID_ARGUMENT", "action 仅支持 create 或 delete。", null);
        }

        var result = await client.PostFormAsync(panelUrl, apiSk, relativePath, fields);
        return result.Success
            ? BtToolSupport.Success("网站备份操作已提交。", BtToolSupport.CreateResponseEnvelope(result))
            : BtToolSupport.Failure("BT_API_ERROR", result.ErrorMessage ?? "网站备份操作失败。", BtToolSupport.CreateResponseEnvelope(result));
    }
}
