using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Cortana.Plugins.Memory.Models;

var src = args.Length > 0 ? args[0] : @"D:\Contrna\cortana.db";
var dst = Path.Combine(Directory.GetCurrentDirectory(), "runner_data", "memory.db");
Console.WriteLine($"Source DB: {src}");
Console.WriteLine($"Target DB: {dst}");

if (!File.Exists(src)) { Console.WriteLine("Source DB not found"); return 1; }

using var sconn = new SqliteConnection($"Data Source={src}");
using var dconn = new SqliteConnection($"Data Source={dst}");

sconn.Open();
dconn.Open();

using (var reset = dconn.CreateCommand())
{
    reset.CommandText = @"DELETE FROM observation_records;
DELETE FROM memory_fragments;
DELETE FROM memory_abstractions;
DELETE FROM memory_mutations;
DELETE FROM memory_events;
DELETE FROM memory_processing_states;";
    reset.ExecuteNonQuery();
}
Console.WriteLine("Target memory tables reset.");

// Try multiple candidate tables in order until we import rows
var candidateTables = new[] { "ChatMessages", "CompactionSegments", "ChatSessions" };
var totalImported = 0;
var targetLimit = 2000;

var sessionAgent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
try
{
    using var mcmd = sconn.CreateCommand();
    mcmd.CommandText = "SELECT Id, AgentId FROM ChatSessions;";
    using var mr = mcmd.ExecuteReader();
    while (mr.Read())
    {
        var sid = mr.IsDBNull(0) ? string.Empty : mr.GetString(0);
        var aid = mr.IsDBNull(1) ? string.Empty : mr.GetString(1);
        if (!string.IsNullOrWhiteSpace(sid) && !string.IsNullOrWhiteSpace(aid) && !sessionAgent.ContainsKey(sid)) sessionAgent[sid] = aid;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Session-Agent map unavailable: {ex.Message}");
}

foreach (var table in candidateTables)
{
    try
    {
        using var scmd = sconn.CreateCommand();
        // import latest rows first to prefer high-quality recent data
        scmd.CommandText = $"SELECT * FROM {table} ORDER BY CreatedTimestamp DESC LIMIT {targetLimit};";
        using var reader = scmd.ExecuteReader();
        var cols = new List<string>();
        for (int i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));

        var list = new List<ObservationRecord>();
        while (reader.Read())
        {
            // heuristic mapping
            string GetStringSafe(string name)
            {
                var idx = cols.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
                return idx >= 0 && !reader.IsDBNull(idx) ? reader.GetValue(idx).ToString() ?? string.Empty : string.Empty;
            }

            long GetLongSafe(string name)
            {
                var idx = cols.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && !reader.IsDBNull(idx))
                {
                    var v = reader.GetValue(idx);
                    if (v is long l) return l;
                    if (long.TryParse(v.ToString(), out var p)) return p;
                }
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            var id = GetStringSafe("id");
            if (string.IsNullOrWhiteSpace(id)) id = GetStringSafe("messageid") ?? GetStringSafe("MessageId") ?? Guid.NewGuid().ToString("N");
            var sessionId = string.IsNullOrWhiteSpace(GetStringSafe("sessionid")) ? Guid.NewGuid().ToString("N") : GetStringSafe("sessionid");
            var content = string.IsNullOrWhiteSpace(GetStringSafe("content")) ? (string.IsNullOrWhiteSpace(GetStringSafe("contentsjson")) ? GetStringSafe("body") : GetStringSafe("contentsjson")) : GetStringSafe("content");
            content = Regex.Replace(content, "^\\[[^\\]]+\\]\\s*", string.Empty).Trim();
            var agentId = string.IsNullOrWhiteSpace(GetStringSafe("agentid")) ? sessionAgent.GetValueOrDefault(sessionId, "global") : GetStringSafe("agentid");
            var r = new ObservationRecord
            {
                Id = id,
                AgentId = agentId,
                WorkspaceId = string.IsNullOrWhiteSpace(GetStringSafe("workspaceid")) ? null : GetStringSafe("workspaceid"),
                SessionId = sessionId,
                TurnId = string.IsNullOrWhiteSpace(GetStringSafe("turnid")) ? null : GetStringSafe("turnid"),
                MessageId = string.IsNullOrWhiteSpace(GetStringSafe("messageid")) ? null : GetStringSafe("messageid"),
                EventType = string.IsNullOrWhiteSpace(GetStringSafe("eventtype")) ? null : GetStringSafe("eventtype"),
                Role = GetStringSafe("role"),
                Content = content,
                AttachmentsJson = string.IsNullOrWhiteSpace(GetStringSafe("attachments")) ? "[]" : GetStringSafe("attachments"),
                CreatedTimestamp = GetLongSafe("createdtimestamp"),
                ModelName = string.IsNullOrWhiteSpace(GetStringSafe("modelname")) ? null : GetStringSafe("modelname"),
                TraceId = string.IsNullOrWhiteSpace(GetStringSafe("traceid")) ? null : GetStringSafe("traceid"),
                SourceFactsJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            };
            list.Add(r);
        }

        Console.WriteLine($"Read {list.Count} rows from source table {table}.");

        if (list.Count > 0)
        {
            using var tx = dconn.BeginTransaction();
            foreach (var r in list)
            {
                using var cmd = dconn.CreateCommand();
                cmd.CommandText = "INSERT OR IGNORE INTO observation_records (id, agentId, workspaceId, sessionId, turnId, messageId, eventType, role, content, attachments, createdTimestamp, modelName, traceId, sourceFacts, schemaVersion, recordVersion, createdAt) VALUES (@id,@agent,@workspace,@sid,@turn,@mid,@etype,@role,@content,@atts,@ts,@model,@trace,@facts,@schema,@record,@created)";
                cmd.Parameters.AddWithValue("@id", r.Id);
                cmd.Parameters.AddWithValue("@agent", (object?)r.AgentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@workspace", (object?)r.WorkspaceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@sid", r.SessionId);
                cmd.Parameters.AddWithValue("@turn", (object?)r.TurnId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@mid", (object?)r.MessageId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@etype", (object?)r.EventType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@role", r.Role);
                cmd.Parameters.AddWithValue("@content", (object?)r.Content ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@atts", r.AttachmentsJson);
                cmd.Parameters.AddWithValue("@ts", r.CreatedTimestamp);
                cmd.Parameters.AddWithValue("@model", (object?)r.ModelName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@trace", (object?)r.TraceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@facts", r.SourceFactsJson);
                cmd.Parameters.AddWithValue("@schema", r.SchemaVersion);
                cmd.Parameters.AddWithValue("@record", r.RecordVersion);
                cmd.Parameters.AddWithValue("@created", r.CreatedAt);
                try { cmd.ExecuteNonQuery(); totalImported++; } catch (Exception ex) { Console.WriteLine($"Insert failed: {ex.Message}"); }
            }
            tx.Commit();
            Console.WriteLine($"Inserted {list.Count} rows from {table} into target observation_records.");
        }

        if (totalImported >= targetLimit) break;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error reading table {table}: {ex.Message}");
    }
}

Console.WriteLine($"Total imported rows: {totalImported}");

return 0;