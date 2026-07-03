namespace Netor.Cortana.Platform.Admin.Models.Dashboard;

public sealed record DashboardMetric(string Title, string Value, string Description, string Icon, string Color);

public sealed record DashboardQuickLink(string Title, string Description, string Controller, string Action, string Icon);

public sealed record DashboardTrendPoint(string Label, int OrderCount, decimal IncomeAmount);

public sealed record DashboardChartSegment(string Label, decimal Value, string Color);

public sealed record DashboardRuntimeInfo(string Label, string Value, string Description, string Icon);

public sealed record DashboardAssetRankItem(string Id, string Name, string Slug, int DownloadCount, string StatusName);

public sealed record DashboardOrderItem(string Id, string No, string Title, string AccountName, decimal Money, string PayStatusName, string PayTimeText);

public sealed record DashboardReviewItem(string Id, string AssetName, string SubmitterName, string StatusName, string SubmittedAtText);

public sealed class DashboardViewModel
{
    public IReadOnlyList<DashboardMetric> Metrics { get; init; } = [];

    public IReadOnlyList<DashboardQuickLink> QuickLinks { get; init; } = [];

    public IReadOnlyList<DashboardTrendPoint> OrderTrend { get; init; } = [];

    public IReadOnlyList<DashboardChartSegment> AssetStatusSegments { get; init; } = [];

    public IReadOnlyList<DashboardChartSegment> HotAssetDownloadSegments { get; init; } = [];

    public IReadOnlyList<DashboardRuntimeInfo> RuntimeInfos { get; init; } = [];

    public IReadOnlyList<DashboardAssetRankItem> HotAssets { get; init; } = [];

    public IReadOnlyList<DashboardOrderItem> LatestOrders { get; init; } = [];

    public IReadOnlyList<DashboardReviewItem> ReviewQueue { get; init; } = [];
}
