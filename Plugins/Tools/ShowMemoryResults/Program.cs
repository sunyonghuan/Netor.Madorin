using Microsoft.Data.Sqlite;

var db = Path.Combine(Directory.GetCurrentDirectory(), "runner_data", "memory.db");
Console.WriteLine($"DB: {db}");

using var conn = new SqliteConnection($"Data Source={db}");
conn.Open();

Console.WriteLine("\nFragments:");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT memoryType, topic, substr(summary,1,200), confidence FROM memory_fragments ORDER BY updatedAt DESC LIMIT 5";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        Console.WriteLine($"type={reader.GetString(0)} topic={reader.GetString(1)} confidence={reader.GetDouble(3):0.00}");
        Console.WriteLine($"summary={reader.GetString(2)}");
        Console.WriteLine("---");
    }
}

Console.WriteLine("\nAbstractions:");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT abstractionType, title, substr(summary,1,240), confidence FROM memory_abstractions ORDER BY updatedAt DESC LIMIT 5";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        Console.WriteLine($"type={reader.GetString(0)} title={reader.GetString(1)} confidence={reader.GetDouble(3):0.00}");
        Console.WriteLine($"summary={reader.GetString(2)}");
        Console.WriteLine("---");
    }
}
