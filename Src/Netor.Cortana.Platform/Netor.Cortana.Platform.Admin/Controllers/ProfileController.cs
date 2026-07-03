using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Netor.Cortana.Platform.Admin.Mcp;
using Netor.Cortana.Platform.Admin.Models.Profile;

namespace Netor.Cortana.Platform.Admin.Controllers;

public sealed class ProfileController(AdminMcpTokenService tokenService, IConfiguration configuration) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Mcp(CancellationToken cancellationToken)
    {
        var managerId = GetCurrentManagerId();
        if (managerId is null)
        {
            return Challenge();
        }

        var model = new McpTokenViewModel
        {
            Tokens = await tokenService.ListByManagerAsync(managerId, cancellationToken),
            PlainToken = TempData["McpPlainToken"] as string,
            PlainTokenNote = TempData["McpPlainTokenNote"] as string,
            EndpointUrl = BuildAbsoluteUrl("/mcp"),
            HealthUrl = BuildAbsoluteUrl("/mcp/health"),
            Enabled = configuration.GetValue("Mcp:Enabled", false)
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(string? note, CancellationToken cancellationToken)
    {
        var managerId = GetCurrentManagerId();
        if (managerId is null)
        {
            return Challenge();
        }

        var plainToken = await tokenService.CreateAsync(managerId, note, cancellationToken);
        TempData["McpPlainToken"] = plainToken;
        TempData["McpPlainTokenNote"] = string.IsNullOrWhiteSpace(note) ? "未命名令牌" : note.Trim();
        TempData["AdminToast"] = "MCP 令牌已生成，请立即保存。";
        return RedirectToAction(nameof(Mcp));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(string id, CancellationToken cancellationToken)
    {
        var managerId = GetCurrentManagerId();
        if (managerId is null)
        {
            return Challenge();
        }

        TempData["AdminToast"] = await tokenService.RevokeAsync(managerId, id, cancellationToken)
            ? "MCP 令牌已吊销。"
            : "MCP 令牌不存在或已被删除。";
        return RedirectToAction(nameof(Mcp));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(string id, bool enabled, CancellationToken cancellationToken)
    {
        var managerId = GetCurrentManagerId();
        if (managerId is null)
        {
            return Challenge();
        }

        TempData["AdminToast"] = await tokenService.ToggleAsync(managerId, id, enabled, cancellationToken)
            ? enabled ? "MCP 令牌已启用。" : "MCP 令牌已停用。"
            : "MCP 令牌不存在或已被删除。";
        return RedirectToAction(nameof(Mcp));
    }

    private string? GetCurrentManagerId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier);

    private string BuildAbsoluteUrl(string path)
    {
        var request = HttpContext.Request;
        var normalizedPath = path.StartsWith('/') ? path : "/" + path;
        return $"{request.Scheme}://{request.Host}{normalizedPath}";
    }
}
