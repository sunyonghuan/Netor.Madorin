using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record RuntimeProviderMessage(string Role, ContentBlock[] Content);

public static class RuntimeProviderRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}
