internal sealed record ReferenceHostConfiguration(
    string HostInstanceId,
    ReferenceHostConfiguration.RuntimeEndpoint[] Runtimes)
{
    private const int RuntimeCount = 3;

    public static async Task<ReferenceHostConfiguration> ReadAsync(
        string[] args,
        TextReader secretReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(secretReader);
        var values = ParseOptions(args);
        var hostInstanceId = GetRequired(values, "--host");
        var runtimes = new RuntimeEndpoint[RuntimeCount];
        for (var index = 0; index < runtimes.Length; index++)
        {
            var suffix = (index + 1).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            var secret = await secretReader.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException(
                    $"Missing handshake secret for Runtime {index}.");
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new ArgumentException(
                    $"Handshake secret for Runtime {index} must not be empty.",
                    nameof(secretReader));
            }

            runtimes[index] = new RuntimeEndpoint(
                Path.GetFullPath(GetRequired(values, "--workspace-" + suffix)),
                GetRequired(values, "--pipe-" + suffix),
                GetRequired(values, "--instance-" + suffix),
                secret);
        }

        return new ReferenceHostConfiguration(hostInstanceId, runtimes);
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length == 0 || args.Length % 2 != 0)
        {
            throw new ArgumentException(
                "reference-host requires option/value pairs.",
                nameof(args));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal)
                || !values.TryAdd(option, args[index + 1]))
            {
                throw new ArgumentException(
                    $"Invalid or duplicate reference-host option '{option}'.",
                    nameof(args));
            }
        }

        return values;
    }

    private static string GetRequired(
        Dictionary<string, string> values,
        string option)
    {
        if (!values.TryGetValue(option, out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"Missing required reference-host option '{option}'.",
                nameof(values));
        }

        return value;
    }

    internal sealed record RuntimeEndpoint(
        string Workspace,
        string PipeName,
        string RuntimeInstanceId,
        string Secret);
}
