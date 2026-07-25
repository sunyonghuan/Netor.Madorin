using Madorin.AI.Runtime.Contracts;

if (args.Length > 0
    && args[0].Equals("multi-instance", StringComparison.OrdinalIgnoreCase))
{
    return await MultiInstanceHost.RunAsync(args[1..]).ConfigureAwait(false);
}

if (args.Length > 0
    && args[0].Equals("reference-host", StringComparison.OrdinalIgnoreCase))
{
    return await ReferenceHost.RunAsync(args[1..]).ConfigureAwait(false);
}

Console.WriteLine(
    $"Madorin.AI.Runtime.SampleHost (protocol {ProtocolVersions.Current}).");
return 0;
