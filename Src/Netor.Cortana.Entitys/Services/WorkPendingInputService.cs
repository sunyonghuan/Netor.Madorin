using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 用户在执行中插话的输入队列服务（WorkPendingInputs 表）。
/// 主智能体通过 <c>check_pending_user_input</c> 工具消费这个队列。
/// </summary>
public sealed class WorkPendingInputService
{
    private readonly CortanaDbContext _db;

    public WorkPendingInputService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>追加一条用户输入到队列。</summary>
    public void Enqueue(string taskId, string content)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO WorkPendingInputs (TaskId, Content, EnqueuedAt, Consumed)
            VALUES (@TaskId, @Content, @EnqueuedAt, 0)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@TaskId", taskId);
                cmd.Parameters.AddWithValue("@Content", content);
                cmd.Parameters.AddWithValue("@EnqueuedAt", now);
            });
    }

    /// <summary>取出最早未消费的输入并标为已消费（FIFO）。无未消费时返回 null。</summary>
    public string? Dequeue(string taskId)
    {
        // 用单条事务保证 SELECT + UPDATE 原子（避免并发重复消费）
        string? content = null;
        _db.ExecuteInTransaction(_ =>
        {
            var row = _db.QueryFirstOrDefault("""
                SELECT Id, Content FROM WorkPendingInputs
                WHERE TaskId = @TaskId AND Consumed = 0
                ORDER BY EnqueuedAt ASC LIMIT 1
                """,
                r => new PendingRow(r.GetInt64(0), r.GetString(1)),
                cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));

            if (row is null) return;

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            _db.Execute("""
                UPDATE WorkPendingInputs SET Consumed = 1, ConsumedAt = @Now WHERE Id = @Id
                """,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Id", row.Id);
                    cmd.Parameters.AddWithValue("@Now", now);
                });

            content = row.Content;
        });
        return content;
    }

    /// <summary>查询任务是否有未消费的输入（不取出）。</summary>
    public bool HasPending(string taskId)
    {
        var count = _db.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM WorkPendingInputs WHERE TaskId = @TaskId AND Consumed = 0",
            cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));
        return count > 0;
    }

    /// <summary>取出并消费当前任务全部未消费输入，按入队顺序返回。</summary>
    public List<string> Drain(string taskId)
    {
        var contents = new List<string>();
        _db.ExecuteInTransaction(_ =>
        {
            var rows = _db.Query(
                """
                SELECT Id, Content FROM WorkPendingInputs
                WHERE TaskId = @TaskId AND Consumed = 0
                ORDER BY EnqueuedAt ASC
                """,
                r => new PendingRow(r.GetInt64(0), r.GetString(1)),
                cmd => cmd.Parameters.AddWithValue("@TaskId", taskId));

            if (rows.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            foreach (var row in rows)
            {
                _db.Execute(
                    """
                    UPDATE WorkPendingInputs SET Consumed = 1, ConsumedAt = @Now WHERE Id = @Id
                    """,
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@Id", row.Id);
                        cmd.Parameters.AddWithValue("@Now", now);
                    });

                contents.Add(row.Content);
            }
        });

        return contents;
    }

    private sealed record PendingRow(long Id, string Content);
}
