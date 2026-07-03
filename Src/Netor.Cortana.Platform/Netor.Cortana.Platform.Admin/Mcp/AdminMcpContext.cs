using System.Security.Claims;
using Netor.Cortana.Platform.Admin.Operations;

namespace Netor.Cortana.Platform.Admin.Mcp;

public sealed class AdminMcpContext(IHttpContextAccessor httpContextAccessor)
{
    private const int SuperAdminPower = 100;

    private HttpContext? HttpContext => httpContextAccessor.HttpContext;

    public string ManagerId => FindClaimValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    public long ManagerNo => long.TryParse(FindClaimValue("ManagerNo"), out var value) ? value : 0;

    public string LoginUserName => FindClaimValue(ClaimTypes.Name) ?? string.Empty;

    public string TokenId => FindClaimValue("TokenID") ?? string.Empty;

    public int RolePower => int.TryParse(FindClaimValue("RolePower"), out var value) ? value : 0;

    public string? RemoteIp => HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => HttpContext?.Request.Headers.UserAgent.ToString();

    public OperationResult<T>? RequireSuperAdmin<T>()
    {
        if (RolePower >= SuperAdminPower)
        {
            return null;
        }

        return OperationResult<T>.Fail(OperationErrorCodes.Forbidden, "当前管理员权限不足。");
    }

    private string? FindClaimValue(string claimType)
        => HttpContext?.User.FindFirstValue(claimType);
}
