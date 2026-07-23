using System.Text.Json;

namespace Madorin.AI.Runtime.Contracts;

/// <summary>Describes one versioned tool exposed across the Runtime protocol.</summary>
public sealed record ToolCatalogItem(
    string ToolId,
    string DisplayName,
    string Description,
    JsonElement InputSchema,
    JsonElement OutputSchema,
    ToolRiskLevel Risk,
    int TimeoutSeconds,
    string[] Tags,
    string[] Capabilities,
    ToolExecutionTarget ExecutionTarget,
    bool RequiresApproval,
    bool IsIdempotent);

/// <summary>Atomically replaces the complete host-owned portion of the tool catalog.</summary>
public sealed record ToolCatalogReplaceRequest(
    string CatalogVersion,
    ToolCatalogItem[] Tools);

/// <summary>Applies a version-checked update to the host-owned portion of the tool catalog.</summary>
public sealed record ToolCatalogPatchRequest(
    string BaseVersion,
    string NewVersion,
    ToolCatalogItem[] Upserts,
    string[] Removals);

/// <summary>Reports the effective merged catalog after a replace or patch.</summary>
public sealed record ToolCatalogUpdateResponse(
    string CatalogVersion,
    string EffectiveVersion,
    int ToolCount);
