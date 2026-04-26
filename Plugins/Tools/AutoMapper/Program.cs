using Microsoft.Data.Sqlite;
using System.Text.Json;

var src = Path.Combine(Directory.GetCurrentDirectory(), "runner_data", "memory.db");
Console.WriteLine($"Scanning source DB: {src}");
if (!File.Exists(src)) { Console.WriteLine("DB not found"); return 1; }

using var conn = new SqliteConnection($"Data Source={src}");
conn.Open();

// enumerate tables and columns
var tables = new List<(string name, List<string> cols)>();
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var name = reader.GetString(0);
        using var c2 = conn.CreateCommand();
        c2.CommandText = $"PRAGMA table_info('{name}');";
        using var r2 = c2.ExecuteReader();
        var cols = new List<string>();
        while (r2.Read()) cols.Add(r2.GetString(1));
        tables.Add((name, cols));
    }
}

// scoring: guess candidate tables containing message content
Console.WriteLine("\nCandidate tables with possible message columns:");
var candidates = new List<(string table, string col, int score)>();
foreach (var t in tables)
{
    foreach (var col in t.cols)
    {
        var lower = col.ToLowerInvariant();
        var score = 0;
        if (lower.Contains("content") || lower.Contains("message") || lower.Contains("text") || lower.Contains("transcript") || lower.Contains("body")) score += 5;
        if (lower.Contains("session") || lower.Contains("sessionid")) score += 2;
        if (lower.Contains("agent") || lower.Contains("user") || lower.Contains("participant")) score += 2;
        if (lower.Contains("time") || lower.Contains("timestamp") || lower.Contains("created")) score += 2;
        if (lower.Contains("id") && !lower.Equals("id")) score += 1;
        if (score > 0) candidates.Add((t.name, col, score));
    }
}

var grouped = candidates.GroupBy(c => c.table).Select(g => new { Table = g.Key, Score = g.Sum(x => x.score), Columns = g.Select(x => (x.col,x.score)).ToList() }).OrderByDescending(x => x.Score).ToList();

if (grouped.Count == 0)
{
    Console.WriteLine("No candidate tables found.");
    return 0;
}

foreach (var g in grouped.Take(10))
{
    Console.WriteLine($"Table: {g.Table}, score={g.Score}");
    foreach (var c in g.Columns.OrderByDescending(x=>x.score)) Console.WriteLine($"  - {c.col} (score {c.score})");
}

// prepare suggested mapping for top table
var top = grouped.First();
var suggested = new Dictionary<string,string>();
// content
var contentCol = top.Columns.FirstOrDefault(c => c.col.ToLowerInvariant().Contains("content") || c.col.ToLowerInvariant().Contains("message") || c.col.ToLowerInvariant().Contains("text") || c.col.ToLowerInvariant().Contains("transcript") || c.col.ToLowerInvariant().Contains("body")).col;
if (!string.IsNullOrWhiteSpace(contentCol)) suggested["content"] = contentCol;
// session
var sessionCol = top.Columns.FirstOrDefault(c => c.col.ToLowerInvariant().Contains("session")).col;
if (!string.IsNullOrWhiteSpace(sessionCol)) suggested["sessionId"] = sessionCol;
// agent
var agentCol = top.Columns.FirstOrDefault(c => c.col.ToLowerInvariant().Contains("agent") || c.col.ToLowerInvariant().Contains("user") || c.col.ToLowerInvariant().Contains("participant")).col;
if (!string.IsNullOrWhiteSpace(agentCol)) suggested["agentId"] = agentCol;
// created
var createdCol = top.Columns.FirstOrDefault(c => c.col.ToLowerInvariant().Contains("created") || c.col.ToLowerInvariant().Contains("timestamp") || c.col.ToLowerInvariant().Contains("time")).col;
if (!string.IsNullOrWhiteSpace(createdCol)) suggested["createdTimestamp"] = createdCol;

Console.WriteLine("\nSuggested mapping for table " + top.Table + ":");
Console.WriteLine(JsonSerializer.Serialize(new { table = top.Table, mapping = suggested }, new JsonSerializerOptions { WriteIndented = true }));

// show sample rows
Console.WriteLine("\nSample rows from suggested table:");
using (var cmd = conn.CreateCommand())
{
    cmd.CommandText = $"SELECT * FROM {top.Table} LIMIT 5;";
    using var r = cmd.ExecuteReader();
    while (r.Read())
    {
        var vals = new List<string>();
        for (int i = 0; i < r.FieldCount; i++) vals.Add(r.IsDBNull(i)?"NULL":r.GetValue(i).ToString());
        Console.WriteLine(string.Join(" | ", vals));
    }
}

return 0;
