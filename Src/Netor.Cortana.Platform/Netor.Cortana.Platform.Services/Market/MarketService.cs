using System.Data;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Services.Market;

public sealed class MarketService(PlatformDbContext dbContext)
{
    public async Task<IReadOnlyList<MarketAssetListItem>> GetPublishedAssetsAsync(CancellationToken cancellationToken = default)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State == ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ID, Type, Name, ShortDescription, IconUrl, CoverUrl
                FROM Assets
                WHERE Status = @status
                ORDER BY PublishedAtUtc DESC
                """;

            var statusParameter = command.CreateParameter();
            statusParameter.ParameterName = "@status";
            statusParameter.Value = (int)AssetStatus.Published;
            command.Parameters.Add(statusParameter);

            var assets = new List<MarketAssetListItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                assets.Add(new MarketAssetListItem(
                    reader.GetString(0),
                    ((AssetType)reader.GetInt32(1)).ToString(),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }

            return assets;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}

public sealed record MarketAssetListItem(string Id, string Type, string Name, string ShortDescription, string? IconUrl, string? CoverUrl);
