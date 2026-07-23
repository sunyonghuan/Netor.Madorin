using System.Diagnostics;
using System.Text.Json;
using Madorin.AI.Runtime.Persistence.Files;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class ConversationStorePerformanceTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    [TestCategory("Performance")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AppendAndReadPageAsync_WithFiveThousandOtherHistories_RemainsBounded()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            for (var index = 0; index < 100; index++)
            {
                await store.AppendMessageAsync(
                    "target-session",
                    CreateDraft(index),
                    TestContext.CancellationToken);
            }

            var before = await MeasurePagesAsync(store, TestContext.CancellationToken);
            var messagesDirectory = Path.Combine(dataDirectory, "messages");
            for (var index = 0; index < 5000; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(messagesDirectory, $"unrelated-{index}.jsonl"),
                    string.Empty,
                    TestContext.CancellationToken);
            }

            var appendTimer = Stopwatch.StartNew();
            for (var index = 100; index < 125; index++)
            {
                await store.AppendMessageAsync(
                    "target-session",
                    CreateDraft(index),
                    TestContext.CancellationToken);
            }

            appendTimer.Stop();
            var after = await MeasurePagesAsync(store, TestContext.CancellationToken);
            TestContext.WriteLine(
                $"5000 unrelated histories: append 25={appendTimer.Elapsed.TotalMilliseconds:F1} ms; page x100 before={before.TotalMilliseconds:F1} ms, after={after.TotalMilliseconds:F1} ms.");

            Assert.IsLessThan(5000, appendTimer.Elapsed.TotalMilliseconds);
            Assert.IsLessThan(
                Math.Max(2000, before.TotalMilliseconds * 5),
                after.TotalMilliseconds);
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    private static async Task<TimeSpan> MeasurePagesAsync(
        ConversationStore store,
        CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < 100; index++)
        {
            var page = await store.ReadPageAsync(
                "target-session",
                cursor: 90,
                pageSize: 10,
                ct);
            Assert.HasCount(10, page);
        }

        timer.Stop();
        return timer.Elapsed;
    }

    private static ConversationMessageDraft CreateDraft(int index)
    {
        using var document = JsonDocument.Parse(
            $"[{{\"type\":\"text\",\"text\":\"message-{index}\"}}]");
        return new ConversationMessageDraft(
            "invocation-1",
            "agent-1",
            "user",
            document.RootElement.Clone(),
            DateTimeOffset.UnixEpoch.AddSeconds(index + 1));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-conversation-performance",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
