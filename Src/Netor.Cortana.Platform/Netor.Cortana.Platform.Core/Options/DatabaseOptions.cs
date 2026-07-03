namespace Netor.Cortana.Platform.Core.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public const string DefaultConnectionString = "Server=10.10.10.121;Database=Netor.Madorin;User Id=sa;Password=buday123!@#;TrustServerCertificate=True;MultipleActiveResultSets=True";

    public string Provider { get; set; } = "SqlServer";

    public string ConnectionString { get; set; } = DefaultConnectionString;
}