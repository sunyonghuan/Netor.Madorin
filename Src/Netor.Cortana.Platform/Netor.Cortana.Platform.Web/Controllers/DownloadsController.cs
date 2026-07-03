using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Netor.Cortana.Platform.Services.Downloads;

namespace Netor.Cortana.Platform.Web.Controllers;

[Authorize]
[Route("downloads")]
public sealed class DownloadsController(DownloadService downloadService) : WebControllerBase
{
    [HttpGet("{assetId}")]
    public async Task<IActionResult> Download(string assetId, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId;
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return Challenge();
        }

        var result = await downloadService.PreparePackageAsync(
            accountId,
            assetId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            cancellationToken);

        if (result.Status == DownloadPackageStatus.Ready && result.Stream is not null)
        {
            TempData["WebToast"] = $"开始下载：{result.AssetName} {result.VersionName}";
            return File(result.Stream, "application/zip", result.FileName);
        }

        if (result.Status == DownloadPackageStatus.NotFound)
        {
            return NotFound();
        }

        if (result.Status == DownloadPackageStatus.AccountDisabled)
        {
            TempData["WebToast"] = "账号已被禁用或冻结，暂时不能下载资源。";
            return RedirectToAction("Index", "UserCenter");
        }

        TempData["WebToast"] = result.Status switch
        {
            DownloadPackageStatus.NoVersion => "该资源还没有可下载版本。",
            DownloadPackageStatus.SubscriptionRequired => "请先订阅该资源后再下载。",
            DownloadPackageStatus.FileMissing => "资源包文件不存在，请联系平台管理员补充包文件。",
            DownloadPackageStatus.PackageIntegrityFailed => "资源包校验失败，已暂停下载，请联系平台管理员重新上架。",
            _ => "暂时无法下载该资源。"
        };

        return RedirectToAction("Details", "Market", new { slug = result.AssetSlug });
    }
}
