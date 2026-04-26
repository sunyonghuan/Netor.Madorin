using Netor.Cortana.Entitys;

int port = 0;
if (args.Length < 1 || !int.TryParse(args[0], out port) || port <= 0)
{
    Console.Error.WriteLine("Usage: FeedSettingsCli <port>");
    Environment.Exit(2);
}

using var db = new CortanaDbContext();

var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

// upsert ConversationFeed.Port into SystemSettings
var exists = db.ExecuteScalar<long>("SELECT COUNT(1) FROM SystemSettings WHERE Id=@Id", cmd => cmd.Parameters.AddWithValue("@Id", "ConversationFeed.Port"));
if (exists > 0)
{
    db.Execute("UPDATE SystemSettings SET Value=@Value, UpdatedTimestamp=@Now WHERE Id=@Id", cmd => {
        cmd.Parameters.AddWithValue("@Value", port.ToString());
        cmd.Parameters.AddWithValue("@Now", now);
        cmd.Parameters.AddWithValue("@Id", "ConversationFeed.Port");
    });
}
else
{
    db.Execute(@"INSERT INTO SystemSettings (Id, CreatedTimestamp, UpdatedTimestamp, [Group], DisplayName, Description, Value, DefaultValue, ValueType, SortOrder)
                VALUES (@Id, @Now, @Now, '网络', 'Conversation Feed 端口', '对话事实流 WebSocket 端口', @Value, @Value, 'int', 0)", cmd => {
        cmd.Parameters.AddWithValue("@Id", "ConversationFeed.Port");
        cmd.Parameters.AddWithValue("@Now", now);
        cmd.Parameters.AddWithValue("@Value", port.ToString());
    });
}

Console.WriteLine($"ConversationFeed.Port set to {port}");
