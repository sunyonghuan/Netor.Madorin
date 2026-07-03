using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Netor.Cortana.Platform.Admin.Mcp;

public sealed class AdminMcpAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AdminMcpTokenService tokenService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AdminMcp";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return AuthenticateResult.NoResult();
        }

        const string bearerPrefix = "Bearer ";
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("不支持的 MCP 鉴权方式。");
        }

        var rawToken = authorization[bearerPrefix.Length..].Trim();
        var resolveResult = await tokenService.ResolveAsync(rawToken, Context.RequestAborted);
        if (resolveResult is null)
        {
            return AuthenticateResult.Fail("MCP 令牌无效或已停用。");
        }

        var manager = resolveResult.Manager;
        var token = resolveResult.Token;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, manager.ID),
            new(ClaimTypes.Name, manager.LoginUserName),
            new(ClaimTypes.Role, manager.Role?.Name ?? "管理员"),
            new("ManagerNo", manager.No.ToString()),
            new("RolePower", (manager.Role?.Power ?? 0).ToString()),
            new("TokenID", token.ID),
            new("AuthMethod", SchemeName)
        };

        await tokenService.TouchLastUsedAsync(
            token.ID,
            Context.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            Context.RequestAborted);

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
