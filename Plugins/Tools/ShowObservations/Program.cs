using Microsoft.Data.Sqlite;

var db = Path.Combine(Directory.GetCurrentDirectory(), "runner_data", "memory.db");
Console.WriteLine($"DB: {db}");
if (!File.Exists(db)) { Console.WriteLine("DB not found"); return 1; }

using var conn = new SqliteConnection($"Data Source={db}");
conn.Open();

using var cmd = conn.CreateCommand();
cmd.CommandText = "SELECT id, agentId, workspaceId, sessionId, turnId, messageId, eventType, role, content as full_content, length(content) as len, createdTimestamp, modelName, traceId, sourceFacts FROM observation_records ORDER BY createdTimestamp DESC LIMIT 20;";
using var reader = cmd.ExecuteReader();
var i = 0;
while (reader.Read())
{
    i++;
    var id = reader.IsDBNull(0)?"":reader.GetString(0);
    var agent = reader.IsDBNull(1)?"":reader.GetString(1);
    var workspace = reader.IsDBNull(2)?"":reader.GetString(2);
    var session = reader.IsDBNull(3)?"":reader.GetString(3);
    var turn = reader.IsDBNull(4)?"":reader.GetString(4);
    var message = reader.IsDBNull(5)?"":reader.GetString(5);
    var eventType = reader.IsDBNull(6)?"":reader.GetString(6);
    var role = reader.IsDBNull(7)?"":reader.GetString(7);
    var contentFull = reader.IsDBNull(8)?"":reader.GetString(8);
    var len = reader.IsDBNull(9)?0:reader.GetInt32(9);
    var ts = reader.IsDBNull(10)?0:reader.GetInt64(10);
    var model = reader.IsDBNull(11)?"":reader.GetString(11);
    var trace = reader.IsDBNull(12)?"":reader.GetString(12);
    var facts = reader.IsDBNull(13)?"":reader.GetString(13);
    Console.WriteLine($"#{i} id={id} agent={agent} session={session} len={len} ts={ts} role={role} eventType={eventType} model={model} trace={trace}\ncontent: {contentFull}\nsourceFacts: {facts}\n---\n");
}

return 0;
