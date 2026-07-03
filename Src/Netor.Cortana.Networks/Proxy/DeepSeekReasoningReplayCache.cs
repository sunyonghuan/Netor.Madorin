using Netor.Cortana.Entitys;

using System.Collections.Concurrent;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// DeepSeek 推理内容回放缓存。
/// </summary>
public sealed class DeepSeekReasoningReplayCache
{
    private const int MaxEntriesPerKey = 32;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, CacheBucket> _cache = new(StringComparer.Ordinal);

    public string BuildKey(AiProviderEntity provider, string exposedModel, string? clientKey)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var client = string.IsNullOrWhiteSpace(clientKey) ? "unknown-client" : clientKey.Trim();
        var model = string.IsNullOrWhiteSpace(exposedModel) ? "unknown-model" : exposedModel.Trim();
        return string.Join('|', provider.Id, model, client);
    }

    public void Append(string cacheKey, string reasoning)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(reasoning))
        {
            return;
        }

        var bucket = _cache.GetOrAdd(cacheKey, _ => new CacheBucket());
        lock (bucket.SyncRoot)
        {
            PruneExpired(bucket, DateTimeOffset.UtcNow);
            bucket.Entries.Enqueue(new ReasoningEntry(DateTimeOffset.UtcNow, reasoning));

            while (bucket.Entries.Count > MaxEntriesPerKey)
            {
                bucket.Entries.Dequeue();
            }
        }
    }

    public string GetReplayText(string cacheKey)
    {
        if (string.IsNullOrWhiteSpace(cacheKey) || !_cache.TryGetValue(cacheKey, out var bucket))
        {
            return string.Empty;
        }

        lock (bucket.SyncRoot)
        {
            PruneExpired(bucket, DateTimeOffset.UtcNow);
            return string.Join("\n\n", bucket.Entries.Select(entry => entry.Text));
        }
    }

    private static void PruneExpired(CacheBucket bucket, DateTimeOffset now)
    {
        while (bucket.Entries.TryPeek(out var entry) && now - entry.CreatedAt > EntryTtl)
        {
            bucket.Entries.Dequeue();
        }
    }

    private sealed class CacheBucket
    {
        public object SyncRoot { get; } = new();

        public Queue<ReasoningEntry> Entries { get; } = new();
    }

    private sealed record ReasoningEntry(DateTimeOffset CreatedAt, string Text);
}
