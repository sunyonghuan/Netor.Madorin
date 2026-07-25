using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class SessionListStage7Tests
{
    private static readonly TimeSpan KeyRetention = TimeSpan.FromDays(1);

    private readonly TestContext _testContext;

    public SessionListStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task ListSessionsAsync_OldOverload_IncludesNonActiveSessions()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var activeId = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-active", KeyRetention, _testContext.CancellationToken);
        var archivedId = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-archived", KeyRetention, _testContext.CancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE sessions SET status = 'Archived' WHERE session_id = $id;";
            cmd.Parameters.AddWithValue("$id", archivedId);
            await cmd.ExecuteNonQueryAsync(_testContext.CancellationToken);
        }

        var results = await repository.ListSessionsAsync(
            cursor: null, pageSize: 50, ct: _testContext.CancellationToken);

        Assert.IsTrue(
            results.Any(s => s.SessionId == activeId),
            "Expected the Active session to be present.");
        Assert.IsTrue(
            results.Any(s => s.SessionId == archivedId),
            "Expected the Archived session to be present when using the old overload.");
    }

    [TestMethod]
    public async Task TrySetInitialSessionTitleAsync_DoesNotChangeUpdatedAt()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-title", KeyRetention, _testContext.CancellationToken);

        var fixedPast = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE sessions SET updated_at = $ts WHERE session_id = $id;";
            cmd.Parameters.AddWithValue("$ts", fixedPast.ToString("O"));
            cmd.Parameters.AddWithValue("$id", sessionId);
            await cmd.ExecuteNonQueryAsync(_testContext.CancellationToken);
        }

        var before = await repository.ListSessionsAsync(
            cursor: null, pageSize: 10, ct: _testContext.CancellationToken);
        var updatedAtBefore = before.Single(s => s.SessionId == sessionId).UpdatedAt;

        var setResult = await repository.TrySetInitialSessionTitleAsync(
            sessionId, "My Title", _testContext.CancellationToken);
        Assert.IsTrue(setResult, "TrySetInitialSessionTitleAsync should have updated the title.");

        var after = await repository.ListSessionsAsync(
            cursor: null, pageSize: 10, ct: _testContext.CancellationToken);
        var updatedAtAfter = after.Single(s => s.SessionId == sessionId).UpdatedAt;

        Assert.AreEqual(
            updatedAtBefore,
            updatedAtAfter,
            "Setting the initial title must not change updated_at.");
    }

    [TestMethod]
    public async Task ListSessionsAsync_FilterByMode_ReturnsOnlyMatching()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var expertId = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-expert-mode", KeyRetention, _testContext.CancellationToken);
        var meetingId = await repository.CreateSessionAsync(
            RuntimeMode.Meeting, "key-meeting-mode", KeyRetention, _testContext.CancellationToken);

        var query = new SessionListQuery(Mode: RuntimeMode.Expert, Status: null, Limit: 50);
        var results = await repository.ListSessionsAsync(query, _testContext.CancellationToken);

        Assert.IsTrue(results.Any(s => s.SessionId == expertId), "Expected Expert session.");
        Assert.IsFalse(results.Any(s => s.SessionId == meetingId), "Meeting session should be filtered out.");
    }

    [TestMethod]
    public async Task ListSessionsAsync_FilterByStatus_ReturnsOnlyMatching()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var activeId = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-active-fs", KeyRetention, _testContext.CancellationToken);
        var archivedId = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-archived-fs", KeyRetention, _testContext.CancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE sessions SET status = 'Archived' WHERE session_id = $id;";
            cmd.Parameters.AddWithValue("$id", archivedId);
            await cmd.ExecuteNonQueryAsync(_testContext.CancellationToken);
        }

        var query = new SessionListQuery(Status: SessionStatus.Archived, Limit: 50);
        var results = await repository.ListSessionsAsync(query, _testContext.CancellationToken);

        Assert.IsTrue(results.Any(s => s.SessionId == archivedId), "Expected Archived session.");
        Assert.IsFalse(results.Any(s => s.SessionId == activeId), "Active session should be filtered out.");
    }

    [TestMethod]
    public async Task ListSessionsAsync_FilterBySince_ReturnsOnlyMatching()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var oldId = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-old-since", KeyRetention, _testContext.CancellationToken);
        var newId = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-new-since", KeyRetention, _testContext.CancellationToken);

        var cutoff = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var oldTime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE sessions SET updated_at = $ts WHERE session_id = $id;";
            cmd.Parameters.AddWithValue("$ts", oldTime.ToString("O"));
            cmd.Parameters.AddWithValue("$id", oldId);
            await cmd.ExecuteNonQueryAsync(_testContext.CancellationToken);
        }

        var query = new SessionListQuery(Status: null, Since: cutoff, Limit: 50);
        var results = await repository.ListSessionsAsync(query, _testContext.CancellationToken);

        Assert.IsFalse(results.Any(s => s.SessionId == oldId), "Old session should be excluded by Since.");
        Assert.IsTrue(results.Any(s => s.SessionId == newId), "Recent session should be included.");
    }

    [TestMethod]
    public async Task ListSessionsAsync_SearchByTitle_ReturnsOnlyMatching()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var id1 = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-search-a", KeyRetention, _testContext.CancellationToken);
        var id2 = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-search-b", KeyRetention, _testContext.CancellationToken);
        var id3 = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-search-c", KeyRetention, _testContext.CancellationToken);

        await repository.TrySetInitialSessionTitleAsync(id1, "Alpha Project", _testContext.CancellationToken);
        await repository.TrySetInitialSessionTitleAsync(id2, "Beta Project", _testContext.CancellationToken);
        await repository.TrySetInitialSessionTitleAsync(id3, "Gamma Task", _testContext.CancellationToken);

        var query = new SessionListQuery(Status: null, Search: "Project", Limit: 50);
        var results = await repository.ListSessionsAsync(query, _testContext.CancellationToken);

        Assert.HasCount(2, results);
        Assert.IsTrue(results.Any(s => s.SessionId == id1));
        Assert.IsTrue(results.Any(s => s.SessionId == id2));
        Assert.IsFalse(results.Any(s => s.SessionId == id3));
    }

    [TestMethod]
    public async Task ListSessionsAsync_SearchTreatsPercentAndUnderscoreAsLiterals()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var id1 = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-literal-pct", KeyRetention, _testContext.CancellationToken);
        var id2 = await repository.CreateSessionAsync(
            RuntimeMode.Expert, "key-literal-normal", KeyRetention, _testContext.CancellationToken);

        await repository.TrySetInitialSessionTitleAsync(id1, "100%_complete", _testContext.CancellationToken);
        await repository.TrySetInitialSessionTitleAsync(id2, "normal title", _testContext.CancellationToken);

        var queryPercent = new SessionListQuery(Status: null, Search: "%", Limit: 50);
        var percentResults = await repository.ListSessionsAsync(queryPercent, _testContext.CancellationToken);
        Assert.HasCount(1, percentResults, "Literal '%' should match exactly one session.");
        Assert.AreEqual(id1, percentResults[0].SessionId);

        var queryUnderscore = new SessionListQuery(Status: null, Search: "_", Limit: 50);
        var underscoreResults = await repository.ListSessionsAsync(queryUnderscore, _testContext.CancellationToken);
        Assert.HasCount(1, underscoreResults, "Literal '_' should match exactly one session.");
        Assert.AreEqual(id1, underscoreResults[0].SessionId);

        var queryCombined = new SessionListQuery(Status: null, Search: "100%_", Limit: 50);
        var combinedResults = await repository.ListSessionsAsync(queryCombined, _testContext.CancellationToken);
        Assert.HasCount(1, combinedResults, "Literal '100%_' should match exactly one session.");
        Assert.AreEqual(id1, combinedResults[0].SessionId);
    }

    [TestMethod]
    public async Task ListSessionsAsync_KeysetPaginationWithSameUpdatedAt_NoDuplicatesNoOmissions()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var fixedTime = new DateTimeOffset(2025, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var sessionIds = new List<string>();

        for (var i = 0; i < 7; i++)
        {
            var id = await repository.CreateSessionAsync(
                RuntimeMode.Expert, $"key-paginate-{i}", KeyRetention, _testContext.CancellationToken);
            sessionIds.Add(id);

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE sessions SET updated_at = $ts WHERE session_id = $id;";
                cmd.Parameters.AddWithValue("$ts", fixedTime.ToString("O"));
                cmd.Parameters.AddWithValue("$id", id);
                await cmd.ExecuteNonQueryAsync(_testContext.CancellationToken);
            }
        }

        var allResults = new List<SessionDescriptor>();
        string? cursor = null;
        var pageCount = 0;

        do
        {
            var page = await repository.ListSessionsAsync(
                new SessionListQuery(Status: null, Cursor: cursor, Limit: 3),
                _testContext.CancellationToken);

            if (page.Count == 0)
            {
                break;
            }

            allResults.AddRange(page);
            pageCount++;

            var last = page[^1];
            cursor = $"{last.UpdatedAt.ToString("O")}|{last.SessionId}";
        }
        while (true);

        var distinctIds = allResults.Select(s => s.SessionId).Distinct().ToList();
        Assert.HasCount(
            allResults.Count, distinctIds,
            "Pagination produced duplicate sessions.");

        Assert.HasCount(
            sessionIds.Count, allResults,
            "Pagination missed some sessions.");

        for (var i = 1; i < allResults.Count; i++)
        {
            var prev = allResults[i - 1];
            var curr = allResults[i];
            Assert.IsGreaterThan(
                0,
                string.Compare(prev.SessionId, curr.SessionId, StringComparison.Ordinal),
                $"Order unstable: {prev.SessionId} should precede {curr.SessionId} under session_id DESC.");
        }

        Assert.IsGreaterThan(1, pageCount, "Expected multiple pages for pagination test.");
    }

    [TestMethod]
    public async Task ListSessionsAsync_InvalidCursorMissingSeparator_Throws()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var query = new SessionListQuery(
            Status: null, Cursor: "nonexistent-session-id", Limit: 10);

        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await repository.ListSessionsAsync(query, _testContext.CancellationToken));
    }

    [TestMethod]
    public async Task ListSessionsAsync_InvalidCursorIllegalTime_Throws()
    {
        await using var connection = await CreateDatabaseAsync(_testContext.CancellationToken);
        var repository = new SqliteSessionRepository(connection);

        var query = new SessionListQuery(
            Status: null, Cursor: "not-a-valid-timestamp|some-session-id", Limit: 10);

        await Assert.ThrowsExactlyAsync<FormatException>(async () =>
            await repository.ListSessionsAsync(query, _testContext.CancellationToken));
    }

    private static async Task<SqliteConnection> CreateDatabaseAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
        return connection;
    }
}
