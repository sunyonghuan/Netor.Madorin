using Netor.EventHub;

using EventArgs = Netor.EventHub.EventArgs;

namespace Netor.Cortana.Store.Models;

public static class StoreEvents
{
    public static AssetInstallProgressEvent OnAssetInstallProgress = new("store.asset.install.progress");

    public static AssetInstallCompletedEvent OnAssetInstallCompleted = new("store.asset.install.completed");

    public static AssetInstallFailedEvent OnAssetInstallFailed = new("store.asset.install.failed");

    public static PlatformSessionExpiredEvent OnPlatformSessionExpired = new("store.platform.session.expired");
}

public record AssetInstallProgressEvent(string Eventid) : EventID<AssetInstallProgressArgs>(Eventid);

public record AssetInstallCompletedEvent(string Eventid) : EventID<AssetInstallCompletedArgs>(Eventid);

public record AssetInstallFailedEvent(string Eventid) : EventID<AssetInstallFailedArgs>(Eventid);

public record PlatformSessionExpiredEvent(string Eventid) : EventID<PlatformSessionExpiredArgs>(Eventid);

public record AssetInstallProgressArgs(string AssetId, string AssetSlug, string Stage, double Progress) : EventArgs;

public record AssetInstallCompletedArgs(string AssetId, string AssetSlug, string InstalledDirectory) : EventArgs;

public record AssetInstallFailedArgs(string AssetId, string AssetSlug, string Message) : EventArgs;

public record PlatformSessionExpiredArgs(string BaseUrl) : EventArgs;
