using System.Text.Json;

namespace Madorin.AI.Runtime.Contracts;

public sealed record RuntimeError(
    string Code,
    string Category,
    string Message,
    bool IsRetryable,
    JsonElement? ProviderDetails,
    string DiagnosticId);
