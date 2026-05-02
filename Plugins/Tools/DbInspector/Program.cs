using System;
using Microsoft.Data.Sqlite;

var db = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
    ? args[0]
    : Path.Combine(Directory.GetCurrentDirectory(), "runner_data", "memory.db");
var summaryOnly = args.Any(static arg => string.Equals(arg, "--summary", StringComparison.OrdinalIgnoreCase));
var compareHostIndex = Array.FindIndex(args, static arg => string.Equals(arg, "--compare-host", StringComparison.OrdinalIgnoreCase));
var compareHost = compareHostIndex >= 0 && compareHostIndex < args.Length - 1 ? args[compareHostIndex + 1] : null;
Console.WriteLine($"Inspecting DB: {db}");

if (!File.Exists(db)) { Console.WriteLine("DB not found"); return 1; }

using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
conn.Open();

string Count(string table)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(1) FROM {table};";
    return cmd.ExecuteScalar()?.ToString() ?? "0";
}

object? Scalar(string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar();
}

bool TableExists(string table)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name=$name;";
    cmd.Parameters.AddWithValue("$name", table);
    return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
}

void PrintScalar(string name, string sql)
{
    try
    {
        Console.WriteLine($"{name}: {Scalar(sql)}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{name}: error - {ex.Message}");
    }
}

void PrintRows(string title, string sql)
{
    Console.WriteLine($"\n{title}:");
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var values = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values.Add(reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i)) ?? string.Empty);
            }
            Console.WriteLine(string.Join(" | ", values));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"error - {ex.Message}");
    }
}

if (summaryOnly)
{
    PrintScalar("observation_records", "SELECT COUNT(*) FROM observation_records;");
    PrintScalar("memory_fragments", "SELECT COUNT(*) FROM memory_fragments;");
    PrintScalar("memory_abstractions", "SELECT COUNT(*) FROM memory_abstractions;");
    PrintScalar("memory_processing_states", "SELECT COUNT(*) FROM memory_processing_states;");
    PrintScalar("obs_agent_empty", "SELECT COUNT(*) FROM observation_records WHERE agentId IS NULL OR trim(agentId)='';");
    PrintScalar("obs_workspace_empty", "SELECT COUNT(*) FROM observation_records WHERE workspaceId IS NULL OR trim(workspaceId)='';");
    PrintScalar("obs_model_empty", "SELECT COUNT(*) FROM observation_records WHERE modelName IS NULL OR trim(modelName)='';");
    PrintScalar("obs_trace_empty", "SELECT COUNT(*) FROM observation_records WHERE traceId IS NULL OR trim(traceId)='';");
    PrintScalar("obs_event_empty", "SELECT COUNT(*) FROM observation_records WHERE eventType IS NULL OR trim(eventType)='';");
    PrintScalar("obs_message_empty", "SELECT COUNT(*) FROM observation_records WHERE messageId IS NULL OR trim(messageId)='';");
    PrintScalar("obs_sourceFacts_empty", "SELECT COUNT(*) FROM observation_records WHERE sourceFacts IS NULL OR trim(sourceFacts)='' OR sourceFacts='{}';");
    PrintScalar("fragments_agent_global", "SELECT COUNT(*) FROM memory_fragments WHERE agentId='global';");
    if (TableExists("ChatMessages"))
    {
        PrintScalar("chat_messages", "SELECT COUNT(*) FROM ChatMessages;");
        PrintScalar("obs_join_chatmessages_by_id", "SELECT COUNT(*) FROM observation_records o JOIN ChatMessages c ON c.Id=o.id;");
        PrintScalar("chat_sessions_agent_empty", "SELECT COUNT(*) FROM ChatSessions WHERE AgentId IS NULL OR trim(AgentId)='';");
        PrintScalar("chat_messages_model_empty", "SELECT COUNT(*) FROM ChatMessages WHERE ModelName IS NULL OR trim(ModelName)='';");
        PrintScalar("chat_sessions_raw_modelid", "SELECT COUNT(*) FROM ChatSessions WHERE RawDiscription LIKE '%modelid%';");
        PrintRows("chat session raw state samples", "SELECT Id, COALESCE(AgentId,'<null>'), substr(RawDiscription,1,160) FROM ChatSessions ORDER BY UpdatedTimestamp DESC LIMIT 5;");
    }
    PrintRows("observation role counts", "SELECT role, COUNT(*) FROM observation_records GROUP BY role ORDER BY COUNT(*) DESC;");
    PrintRows("observation eventType counts", "SELECT COALESCE(eventType,'<null>'), COUNT(*) FROM observation_records GROUP BY eventType ORDER BY COUNT(*) DESC LIMIT 20;");
    PrintRows("observation modelName counts", "SELECT COALESCE(modelName,'<null>'), COUNT(*) FROM observation_records GROUP BY modelName ORDER BY COUNT(*) DESC LIMIT 20;");
    PrintRows("observation agentId counts", "SELECT COALESCE(agentId,'<null>'), COUNT(*) FROM observation_records GROUP BY agentId ORDER BY COUNT(*) DESC LIMIT 20;");
    PrintRows("processing states", "SELECT id, processorName, COALESCE(agentId,'<null>'), COALESCE(workspaceId,'<null>'), state, lastObservationTimestamp, COALESCE(lastObservationId,'<null>'), processedCount, createdFragmentCount, mergedFragmentCount, createdAbstractionCount, COALESCE(lastError,'<null>') FROM memory_processing_states ORDER BY updatedAt DESC LIMIT 10;");
    PrintRows("sample observations", "SELECT id, COALESCE(agentId,'<null>'), COALESCE(workspaceId,'<null>'), sessionId, COALESCE(eventType,'<null>'), role, length(content), createdTimestamp, COALESCE(modelName,'<null>'), COALESCE(traceId,'<null>'), substr(sourceFacts,1,120) FROM observation_records ORDER BY createdTimestamp DESC LIMIT 8;");

    if (!string.IsNullOrWhiteSpace(compareHost))
    {
        if (!File.Exists(compareHost))
        {
            Console.WriteLine($"compare host missing: {compareHost}");
            return 1;
        }

        using var attach = conn.CreateCommand();
        attach.CommandText = "ATTACH DATABASE $host AS host;";
        attach.Parameters.AddWithValue("$host", compareHost);
        attach.ExecuteNonQuery();

        PrintScalar("host_chat_messages", "SELECT COUNT(*) FROM host.ChatMessages;");
        PrintScalar("host_chat_sessions", "SELECT COUNT(*) FROM host.ChatSessions;");
        PrintScalar("host_sessions_agent_empty", "SELECT COUNT(*) FROM host.ChatSessions WHERE AgentId IS NULL OR trim(AgentId)='';");
        PrintScalar("host_sessions_raw_modelid", "SELECT COUNT(*) FROM host.ChatSessions WHERE RawDiscription LIKE '%modelid%';");
        PrintScalar("host_messages_missing_in_memory", "SELECT COUNT(*) FROM host.ChatMessages c LEFT JOIN observation_records o ON o.id = c.Id WHERE o.id IS NULL;");
        PrintScalar("memory_observations_missing_in_host", "SELECT COUNT(*) FROM observation_records o LEFT JOIN host.ChatMessages c ON c.Id = o.id WHERE c.Id IS NULL;");
        PrintScalar("joined_rows", "SELECT COUNT(*) FROM observation_records o JOIN host.ChatMessages c ON c.Id = o.id;");
        PrintScalar("joined_model_mismatch", "SELECT COUNT(*) FROM observation_records o JOIN host.ChatMessages c ON c.Id = o.id WHERE COALESCE(o.modelName,'') <> COALESCE(c.ModelName,'');");
        PrintScalar("joined_role_mismatch", "SELECT COUNT(*) FROM observation_records o JOIN host.ChatMessages c ON c.Id = o.id WHERE COALESCE(o.role,'') <> COALESCE(c.Role,'');");
        PrintRows("host messages missing in memory sample", "SELECT c.Id, c.SessionId, c.Role, length(c.Content), c.CreatedTimestamp, c.ModelName FROM host.ChatMessages c LEFT JOIN observation_records o ON o.id = c.Id WHERE o.id IS NULL ORDER BY c.CreatedTimestamp DESC LIMIT 10;");
        PrintRows("memory observations not from host sample", "SELECT o.Id, o.SessionId, o.Role, length(o.Content), o.CreatedTimestamp, o.ModelName, COALESCE(o.eventType,'<null>') FROM observation_records o LEFT JOIN host.ChatMessages c ON c.Id = o.id WHERE c.Id IS NULL ORDER BY o.CreatedTimestamp DESC LIMIT 10;");
        PrintRows("joined source completeness sample", "SELECT o.Id, COALESCE(o.agentId,'<null>'), COALESCE(s.AgentId,'<session-null>'), o.SessionId, COALESCE(o.eventType,'<null>'), COALESCE(o.messageId,'<null>'), COALESCE(o.traceId,'<null>'), o.ModelName, substr(s.RawDiscription,1,120) FROM observation_records o JOIN host.ChatMessages c ON c.Id=o.Id LEFT JOIN host.ChatSessions s ON s.Id=o.SessionId ORDER BY o.CreatedTimestamp DESC LIMIT 8;");
    }

    return 0;
}

var tables = new[] { "observation_records", "memory_fragments", "memory_abstractions", "memory_events", "memory_mutations" };
foreach (var t in tables)
{
    try { Console.WriteLine($"{t}: {Count(t)}"); } catch (Exception ex) { Console.WriteLine($"{t}: error - {ex.Message}"); }
}
// enumerate all tables in the DB
Console.WriteLine("\nAll tables and row counts:");
using (var tcmd = conn.CreateCommand())
{
    tcmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
    using var treg = tcmd.ExecuteReader();
    var tableNames = new List<string>();
    while (treg.Read()) tableNames.Add(treg.GetString(0));

    foreach (var table in tableNames)
    {
        try { Console.WriteLine($"{table}: {Count(table)}"); } catch (Exception ex) { Console.WriteLine($"{table}: error - {ex.Message}"); }
    }

    Console.WriteLine("\nTop tables sample rows (up to 3 each):");
    foreach (var table in tableNames)
    {
        Console.WriteLine($"\n--- {table} ---");
        try
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"PRAGMA table_info('{table}');";
            using var r = c.ExecuteReader();
            var cols = new List<string>();
            while (r.Read()) cols.Add(r.GetString(1));
            Console.WriteLine($"Columns: {string.Join(',', cols)}");

            using var s = conn.CreateCommand();
            s.CommandText = $"SELECT * FROM {table} LIMIT 3;";
            using var sr = s.ExecuteReader();
            while (sr.Read())
            {
                var values = new List<string>();
                for (int i = 0; i < sr.FieldCount; i++)
                {
                    var v = sr.IsDBNull(i) ? "NULL" : sr.GetValue(i).ToString();
                    values.Add(v?.Replace('\n',' ').Replace('\r',' ') ?? "");
                }
                Console.WriteLine(string.Join(" | ", values));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading table {table}: {ex.Message}");
        }
    }
}

return 0;
