using Netor.Cortana.Platform.Web.Models.Market;

namespace Netor.Cortana.Platform.Web.Models.Home;

public sealed class HomeIndexViewModel
{
    public IReadOnlyList<MarketAssetCardViewModel> FeaturedAssets { get; init; } = [];

    public IReadOnlyList<HomeFeatureItem> Features { get; init; } = [];
}

public sealed record HomeFeatureItem(string Title, string Description, string Icon, string Url);