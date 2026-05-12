namespace Netor.Cortana.Platform.Core.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public const string DefaultConnectionString = "Data Source=../Data/platform.db";

    public string Provider { get; set; } = "Sqlite";

    public string ConnectionString { get; set; } = DefaultConnectionString;
}