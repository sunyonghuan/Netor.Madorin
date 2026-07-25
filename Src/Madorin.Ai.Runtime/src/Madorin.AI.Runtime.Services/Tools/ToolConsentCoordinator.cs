using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

/// <summary>Persists host consent and resumes a pending tool call with a narrowed grant.</summary>
public sealed class ToolConsentCoordinator(
    ToolGateway toolGateway,
    IToolAuthorizationService authorizationService,
    ToolCatalogSnapshot catalogSnapshot,
    Func<ToolPermissionRequest, CancellationToken, Task<ToolPermissionResponse>>?
        toolPermissionHandler = null,
    Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        approvalHandler = null,
    IToolStateStore? toolStateStore = null)
{
    private readonly Func<ApprovalRequest, CancellationToken, Task<ApprovalResponse>>?
        _approvalHandler = approvalHandler;
    private readonly IToolAuthorizationService _authorizationService = authorizationService
        ?? throw new ArgumentNullException(nameof(authorizationService));
    private readonly ToolCatalogSnapshot _catalogSnapshot = catalogSnapshot
        ?? throw new ArgumentNullException(nameof(catalogSnapshot));
    private readonly ToolGateway _toolGateway = toolGateway
        ?? throw new ArgumentNullException(nameof(toolGateway));
    private readonly Func<ToolPermissionRequest, CancellationToken, Task<ToolPermissionResponse>>?
        _toolPermissionHandler = toolPermissionHandler;
    private readonly IToolStateStore? _toolStateStore = toolStateStore;

    public bool CanRequestApproval => _approvalHandler is not null;

    /// <summary>Uses the authenticated host approval channel for a non-tool workflow decision.</summary>
    public Task<ApprovalResponse> RequestWorkflowApprovalAsync(
        ApprovalRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _approvalHandler is null
            ? Task.FromException<ApprovalResponse>(
                new InvalidOperationException("The host does not support workflow approval requests."))
            : _approvalHandler(request, ct);
    }

    public async Task<ToolGatewayResult> ResolveAndExecuteAsync(
        ToolInvocation invocation,
        ToolGatewayResult pendingResult,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(pendingResult);
        if (pendingResult.Kind is not (ToolGatewayResultKind.NeedsApproval
            or ToolGatewayResultKind.NeedsPermission))
        {
            throw new ArgumentException(
                "Tool consent can only resolve a pending approval or permission result.",
                nameof(pendingResult));
        }

        if (!_catalogSnapshot.TryGetTool(invocation.ToolId, out var descriptor)
            || descriptor is null)
        {
            return Failed(
                RuntimeErrorCodes.ToolExecutorUnavailable,
                "Tool consent cannot be resolved because the tool is absent from the fixed catalog snapshot.");
        }

        var correlationId = Guid.NewGuid().ToString("N");
        ToolGrant? grant;
        if (pendingResult.Kind is ToolGatewayResultKind.NeedsApproval)
        {
            if (_approvalHandler is null)
            {
                return Failed(
                    RuntimeErrorCodes.ToolApprovalRequired,
                    "The host does not support tool approval requests.");
            }

            var approvalRequestId = CreateApprovalRequestId(invocation);
            var argumentsHash = ComputeHash(invocation.ArgumentsJson);
            var approvalReason = pendingResult.Error?.Message
                ?? "The tool requires explicit approval.";
            var approvalState = new ToolApprovalState(
                approvalRequestId,
                invocation.CallId,
                invocation.RunId,
                invocation.AgentId,
                invocation.ToolId,
                argumentsHash,
                "pending",
                GrantId: null,
                approvalReason,
                DateTimeOffset.UtcNow,
                DecidedAt: null);
            if (_toolStateStore is not null)
            {
                try
                {
                    await _toolStateStore.SaveApprovalAsync(approvalState, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    return Failed(
                        RuntimeErrorCodes.ToolApprovalRequired,
                        $"The approval request could not be persisted: {ex.Message}");
                }
            }

            var response = await _approvalHandler(
                new ApprovalRequest(
                    correlationId,
                    approvalRequestId,
                    invocation.CallId,
                    invocation.RunId,
                    invocation.AgentId,
                    invocation.ParentAgentId,
                    invocation.ToolId,
                    argumentsHash,
                    descriptor.Risk,
                    $"{descriptor.ExecutionTarget.ToString().ToLowerInvariant()}:{descriptor.ToolId}",
                    approvalReason,
                    invocation.SessionId,
                    invocation.InvocationId,
                    invocation.ParentInvocationId,
                    invocation.WorkStepId,
                    invocation.PlanVersion),
                ct).ConfigureAwait(false);
            if (!string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal)
                || !string.Equals(
                    response.ApprovalRequestId,
                    approvalRequestId,
                    StringComparison.Ordinal))
            {
                return Failed(
                    RuntimeErrorCodes.ToolApprovalRequired,
                    "The host returned an approval response for another request.");
            }

            if (_toolStateStore is not null)
            {
                try
                {
                    await _toolStateStore.SaveApprovalAsync(
                        approvalState with
                        {
                            Decision = FormatDecision(response.Decision),
                            GrantId = response.Grant?.GrantId,
                            Reason = response.Reason,
                            DecidedAt = DateTimeOffset.UtcNow
                        },
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    return Failed(
                        RuntimeErrorCodes.ToolApprovalRequired,
                        $"The approval decision could not be persisted: {ex.Message}");
                }
            }

            if (response.Decision is not ToolAuthorizationDecision.Granted)
            {
                return Failed(
                    RuntimeErrorCodes.ToolApprovalRequired,
                    response.Reason ?? "The tool call was not approved.");
            }

            grant = response.Grant is null
                ? null
                : response.Grant with { ApprovalRequestId = approvalRequestId };
        }
        else
        {
            if (_toolPermissionHandler is null)
            {
                return Failed(
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "The host does not support tool permission requests.");
            }

            var permissionRequestId = CreateConsentRequestId("permission", invocation);
            var response = await _toolPermissionHandler(
                new ToolPermissionRequest(
                    permissionRequestId,
                    invocation.AgentId,
                    invocation.ParentAgentId,
                    invocation.ToolId,
                    invocation.CallId,
                    ComputeHash(invocation.ArgumentsJson),
                    descriptor.Risk.ToString(),
                    "call",
                    pendingResult.Error?.Message ?? "The tool requires a Grant.",
                    correlationId,
                    invocation.RunId,
                    _catalogSnapshot.EffectiveVersion,
                    descriptor.Capabilities?.ToArray(),
                    invocation.SessionId,
                    invocation.InvocationId,
                    invocation.ParentInvocationId,
                    invocation.WorkStepId,
                    invocation.PlanVersion),
                ct).ConfigureAwait(false);
            if (!string.Equals(response.CorrelationId, correlationId, StringComparison.Ordinal)
                || !string.Equals(response.CallId, invocation.CallId, StringComparison.Ordinal))
            {
                return Failed(
                    RuntimeErrorCodes.ToolPermissionRequired,
                    "The host returned a permission response for another tool call.");
            }

            if (response.Decision is not ToolAuthorizationDecision.Granted)
            {
                return Failed(
                    RuntimeErrorCodes.ToolPermissionRequired,
                    response.Reason ?? "The host denied the tool Grant.");
            }

            grant = response.Grant;
        }

        if (grant is null)
        {
            return Failed(
                pendingResult.Kind is ToolGatewayResultKind.NeedsApproval
                    ? RuntimeErrorCodes.ToolApprovalRequired
                    : RuntimeErrorCodes.ToolPermissionRequired,
                "A granted tool decision must include a narrowed Grant.");
        }

        try
        {
            await _authorizationService.SaveGrantAsync(
                grant,
                invocation.AgentId,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Failed(
                RuntimeErrorCodes.ToolPermissionRequired,
                $"The host returned an invalid Grant: {ex.Message}");
        }

        return await _toolGateway.ExecuteAsync(
            invocation,
            permission: null,
            _catalogSnapshot,
            ct).ConfigureAwait(false);
    }

    public static string CreateConsentRequestId(
        ToolGatewayResultKind pendingKind,
        ToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return pendingKind switch
        {
            ToolGatewayResultKind.NeedsApproval => CreateConsentRequestId("approval", invocation),
            ToolGatewayResultKind.NeedsPermission => CreateConsentRequestId("permission", invocation),
            _ => throw new ArgumentOutOfRangeException(
                nameof(pendingKind),
                pendingKind,
                "Only pending approval or permission results have consent request ids.")
        };
    }

    private static string CreateApprovalRequestId(ToolInvocation invocation)
    {
        return CreateConsentRequestId("approval", invocation);
    }

    private static string CreateConsentRequestId(string prefix, ToolInvocation invocation)
    {
        var identity = $"{invocation.RunId}\n{invocation.CallId}";
        return $"{prefix}-{Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))}";
    }

    private static string ComputeHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string FormatDecision(ToolAuthorizationDecision decision) => decision switch
    {
        ToolAuthorizationDecision.Granted => "granted",
        ToolAuthorizationDecision.Denied => "denied",
        ToolAuthorizationDecision.NeedsApproval => "needs-approval",
        _ => throw new ArgumentOutOfRangeException(nameof(decision))
    };

    private static ToolGatewayResult Failed(string code, string message) =>
        new(
            ToolGatewayResultKind.Failed,
            Error: new RuntimeError(
                code,
                "tool",
                message,
                IsRetryable: false,
                ProviderDetails: null,
                Guid.NewGuid().ToString("N")));
}
