using Microsoft.EntityFrameworkCore.ChangeTracking;
using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Entitys.Data;

internal static class PlatformNativeAotRoots
{
    public static void PreserveEfCoreGenericCode()
    {
        _ = ValueComparer.CreateDefault<bool>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<byte>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<DateTime>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<DateTime?>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<DateTimeOffset>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<DateTimeOffset?>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<decimal>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<int>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<long>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<string>(favorStructuralComparisons: false);
        _ = ValueComparer.CreateDefault<byte[]>(favorStructuralComparisons: true);

        _ = Enum.GetValues<AssetReviewStatus>();
        _ = Enum.GetValues<AssetStatus>();
        _ = Enum.GetValues<AssetType>();
        _ = Enum.GetValues<CreatorStatus>();
        _ = Enum.GetValues<DocArticleStatus>();
        _ = Enum.GetValues<NetorDataType>();
        _ = Enum.GetValues<PricingPlanType>();
        _ = Enum.GetValues<SettlementStatus>();
        _ = Enum.GetValues<SubscriptionStatus>();
    }
}
