using System.Diagnostics.Metrics;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

internal static class EventOutboxMetrics
{
    private static readonly Meter Meter = new(SqliteEventOutbox.DiagnosticsMeterName);
    private static readonly Counter<long> Backpressure = Meter.CreateCounter<long>(
        "madorin.runtime.outbox.backpressure",
        "{action}");
    private static readonly Histogram<long> ConfiguredCapacity = Meter.CreateHistogram<long>(
        "madorin.runtime.outbox.configured_capacity",
        "By");
    private static readonly Counter<long> DeltaDropped = Meter.CreateCounter<long>(
        "madorin.runtime.outbox.delta_dropped",
        "{event}");
    private static readonly Counter<long> MemorySpill = Meter.CreateCounter<long>(
        "madorin.runtime.outbox.memory_spill",
        "{event}");
    private static readonly Histogram<long> RetainedBytes = Meter.CreateHistogram<long>(
        "madorin.runtime.outbox.retained",
        "By");

    public static void RecordConfiguredCapacity(long memoryBytes, long diskBytes)
    {
        ConfiguredCapacity.Record(
            memoryBytes,
            new KeyValuePair<string, object?>("tier", "memory"));
        ConfiguredCapacity.Record(
            diskBytes,
            new KeyValuePair<string, object?>("tier", "disk"));
    }

    public static void RecordRetainedBytes(long memoryBytes, long diskBytes)
    {
        RetainedBytes.Record(
            memoryBytes,
            new KeyValuePair<string, object?>("tier", "memory"));
        RetainedBytes.Record(
            diskBytes,
            new KeyValuePair<string, object?>("tier", "disk"));
    }

    public static void RecordDeltaDropped() => DeltaDropped.Add(1);

    public static void RecordMemorySpill() => MemorySpill.Add(1);

    public static void RecordBackpressure(string tier, string reason) =>
        Backpressure.Add(
            1,
            new KeyValuePair<string, object?>("tier", tier),
            new KeyValuePair<string, object?>("reason", reason));
}
