using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

public interface IToolAuthorizationService
{
    public Task<ToolAuthorizationResult> AuthorizeAsync(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        ToolPermissionContext? suppliedPermission = null,
        CancellationToken ct = default);

    public Task SaveGrantAsync(
        ToolGrant grant,
        string? agentId = null,
        CancellationToken ct = default);

    public Task<int> RevokeGrantAsync(
        string grantId,
        bool revokeDescendants = true,
        string? reason = null,
        CancellationToken ct = default);
}
