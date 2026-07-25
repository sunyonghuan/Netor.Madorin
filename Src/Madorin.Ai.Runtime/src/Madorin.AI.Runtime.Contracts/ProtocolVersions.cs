namespace Madorin.AI.Runtime.Contracts;

public static class ProtocolVersions
{
    public const string Current = "1.1";
    public const string Legacy = "1.0";
    public const string DbSchemaVersion = "14";

    public static readonly string[] Supported = [Current, Legacy];

    public static string? Negotiate(IEnumerable<string> clientSupported)
    {
        var clientVersions = clientSupported.ToHashSet(StringComparer.Ordinal);

        foreach (var version in Supported)
        {
            if (clientVersions.Contains(version))
            {
                return version;
            }
        }

        return null;
    }
}
