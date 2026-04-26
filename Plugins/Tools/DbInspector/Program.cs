using System;
using Microsoft.Data.Sqlite;

var db = Path.Combine(Directory.GetCurrentDirectory(), "runner_data", "memory.db");
Console.WriteLine($"Inspecting DB: {db}");

if (!File.Exists(db)) { Console.WriteLine("DB not found"); return 1; }

using var conn = new SqliteConnection($"Data Source={db}");
conn.Open();

string Count(string table)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(1) FROM {table};";
    return cmd.ExecuteScalar()?.ToString() ?? "0";
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
