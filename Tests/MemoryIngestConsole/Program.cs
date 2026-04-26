using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin;
using Cortana.Plugins.Memory.Services;
using Microsoft.Data.Sqlite;

int port = 65321; // use the configured feed port
string dataDir = System.IO.Path.Combine(System.Environment.CurrentDirectory, ".cortana", "plugins", "memory_engine", "data");
System.IO.Directory.CreateDirectory(dataDir);

var settings = new PluginSettings(
    dataDirectory: dataDir,
    workspaceDirectory: System.Environment.CurrentDirectory,
    pluginDirectory: System.IO.Path.GetDirectoryName(System.Environment.ProcessPath) ?? System.Environment.CurrentDirectory,
    wsPort: 0,
    chatWsEndpoint: string.Empty,
    conversationFeedEndpoint: string.Empty,
    conversationFeedProtocol: "conversation-feed",
    conversationFeedVersion: "1.0.0",
    conversationFeedPort: port);

using var host = Host.CreateDefaultBuilder()
    .ConfigureLogging(b => b.ClearProviders().AddConsole().SetMinimumLevel(LogLevel.Information))
    .ConfigureServices(services =>
    {
        services.AddSingleton(settings);
        services.AddSingleton<Cortana.Plugins.Memory.Storage.IMemoryStore, Cortana.Plugins.Memory.Storage.MemoryStore>();
        services.AddHostedService<MemoryIngestService>();
    })
    .Build();

await host.StartAsync();
Console.WriteLine("MemoryIngestConsole started. Waiting 5s...");
await Task.Delay(TimeSpan.FromSeconds(5));

// probe DB row count
var dbPath = System.IO.Path.Combine(dataDir, "memory.db");
using var conn = new SqliteConnection($"Data Source={dbPath}");
conn.Open();
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT COUNT(1) FROM observation_records";
    var count = (long)cmd.ExecuteScalar()!;
    Console.WriteLine($"observation_records count = {count}");
}

// dump schema
Console.WriteLine("Columns:");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "PRAGMA table_info('observation_records')";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var cid = reader.GetInt32(0);
        var name = reader.GetString(1);
        var type = reader.GetString(2);
        var notnull = reader.GetInt32(3);
        var dflt = reader.IsDBNull(4) ? null : reader.GetString(4);
        var pk = reader.GetInt32(5);
        Console.WriteLine($" - {cid}: {name} {type} NOTNULL={notnull} DEFAULT={dflt} PK={pk}");
    }
}

Console.WriteLine("Indexes:");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "PRAGMA index_list('observation_records')";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var seq = reader.GetInt32(0);
        var name = reader.GetString(1);
        var unique = reader.GetInt32(2);
        Console.WriteLine($" - {seq}: {name} UNIQUE={unique}");
    }
}

await host.StopAsync();
