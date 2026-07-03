using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;
using Netor.Cortana.Store.Services;

namespace Netor.Cortana.Store.ViewModels;

public sealed class StoreWindowViewModel(
    IPlatformAccountStore accountStore,
    IPlatformMarketClient marketClient,
    IInstalledAssetStore installedAssetStore,
    IPlatformBaseUrlProvider baseUrlProvider,
    IPackageInstallService packageInstallService) : INotifyPropertyChanged
{
    private const int MarketPageSize = 20;
    private const string AllCategoryFilterText = "全部分类";
    private string _selectedView = "资源市场";
    private string _accountText = "未登录";
    private string _statusText = "应用商店已准备就绪。";
    private string _searchText = string.Empty;
    private string _selectedCategoryFilter = AllCategoryFilterText;
    private AssetType? _marketTypeFilter;
    private bool _isPopularSortActive;
    private bool _isBusy;
    private bool _isSignedIn;
    private bool _isLoadingMoreAssets;
    private bool _hasMoreMarketAssets;
    private int _marketCurrentPage;
    private int _marketTotalCount;
    private IReadOnlyList<StoreAssetItem> _assets = [];
    private IReadOnlyList<string> _categoryFilterOptions = [AllCategoryFilterText];
    private IReadOnlyList<StoreMarketAssetRow> _marketAssetRows = [];
    private IReadOnlyList<StoreInstallManifest> _ownedAssets = [];
    private IReadOnlyList<StoreInstallAssetRow> _ownedAssetRows = [];
    private IReadOnlyList<ApiClientUpdateItem> _updates = [];
    private IReadOnlyList<StoreUpdateAssetRow> _updateRows = [];
    private IReadOnlyList<InstalledAssetRecord> _installedAssets = [];
    private IReadOnlyList<StoreInstalledAssetRow> _installedAssetRows = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SelectedView
    {
        get => _selectedView;
        set => SetField(ref _selectedView, value);
    }

    public string AccountText
    {
        get => _accountText;
        private set => SetField(ref _accountText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set
        {
            if (!SetField(ref _isSignedIn, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsLoginButtonVisible));
            OnPropertyChanged(nameof(IsSignedInActionsVisible));
            OnPropertyChanged(nameof(OwnedEmptyText));
            OnPropertyChanged(nameof(UpdateEmptyText));
            RefreshMarketRows();
            OnListStateChanged();
        }
    }

    public bool IsLoginButtonVisible => !IsSignedIn;

    public bool IsSignedInActionsVisible => IsSignedIn;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
            {
                return;
            }

            OnListStateChanged();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                RefreshMarketRows();
                OnPropertyChanged(nameof(MarketEmptyText));
            }
        }
    }

    public IReadOnlyList<string> CategoryFilterOptions
    {
        get => _categoryFilterOptions;
        private set => SetField(ref _categoryFilterOptions, value);
    }

    public string SelectedCategoryFilter
    {
        get => _selectedCategoryFilter;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? AllCategoryFilterText : value;
            if (SetField(ref _selectedCategoryFilter, next))
            {
                RefreshMarketRows();
                OnPropertyChanged(nameof(MarketEmptyText));
            }
        }
    }

    public bool IsAllMarketFilterActive => _marketTypeFilter is null;

    public bool IsPluginMarketFilterActive => _marketTypeFilter == AssetType.Plugin;

    public bool IsSkillMarketFilterActive => _marketTypeFilter == AssetType.Skill;

    public bool IsAgentMarketFilterActive => _marketTypeFilter == AssetType.Agent;

    public bool IsSolutionMarketFilterActive => _marketTypeFilter == AssetType.Solution;

    public bool IsPopularSortActive => _isPopularSortActive;

    public bool IsMarketRowsVisible => !IsBusy && MarketAssetRows.Count > 0;

    public bool IsMarketEmptyVisible => !IsBusy && MarketAssetRows.Count == 0;

    public bool IsLoadingMoreAssets
    {
        get => _isLoadingMoreAssets;
        private set => SetField(ref _isLoadingMoreAssets, value);
    }

    public bool HasMoreMarketAssets
    {
        get => _hasMoreMarketAssets;
        private set => SetField(ref _hasMoreMarketAssets, value);
    }

    public string MarketEmptyText
    {
        get
        {
            if (Assets.Count == 0)
            {
                return "暂无可展示资源，请确认平台服务已启动并发布资源。";
            }

            return "没有匹配当前搜索、类型或分类筛选的资源。";
        }
    }

    public bool IsOwnedRowsVisible => !IsBusy && OwnedAssetRows.Count > 0;

    public bool IsOwnedEmptyVisible => !IsBusy && OwnedAssetRows.Count == 0;

    public string OwnedEmptyText => IsSignedIn
        ? "当前账号暂无可安装资源，点击同步刷新授权资源。"
        : "登录平台账号后可同步已拥有资源。";

    public bool IsUpdateRowsVisible => !IsBusy && UpdateRows.Count > 0;

    public bool IsUpdateEmptyVisible => !IsBusy && UpdateRows.Count == 0;

    public string UpdateEmptyText => IsSignedIn
        ? "当前没有可用更新。"
        : "登录平台账号后可检查已安装资源更新。";

    public bool IsInstalledRowsVisible => !IsBusy && InstalledAssetRows.Count > 0;

    public bool IsInstalledEmptyVisible => !IsBusy && InstalledAssetRows.Count == 0;

    public string InstalledEmptyText => "本机暂无应用商店安装记录。";

    public IReadOnlyList<StoreAssetItem> Assets
    {
        get => _assets;
        private set
        {
            if (SetField(ref _assets, value))
            {
                RefreshCategoryFilterOptions();
                OnPropertyChanged(nameof(MarketEmptyText));
            }
        }
    }

    public IReadOnlyList<StoreMarketAssetRow> MarketAssetRows
    {
        get => _marketAssetRows;
        private set
        {
            if (SetField(ref _marketAssetRows, value))
            {
                OnPropertyChanged(nameof(IsMarketRowsVisible));
                OnPropertyChanged(nameof(IsMarketEmptyVisible));
            }
        }
    }

    public IReadOnlyList<StoreInstallManifest> OwnedAssets
    {
        get => _ownedAssets;
        private set => SetField(ref _ownedAssets, value);
    }

    public IReadOnlyList<StoreInstallAssetRow> OwnedAssetRows
    {
        get => _ownedAssetRows;
        private set
        {
            if (SetField(ref _ownedAssetRows, value))
            {
                OnPropertyChanged(nameof(IsOwnedRowsVisible));
                OnPropertyChanged(nameof(IsOwnedEmptyVisible));
            }
        }
    }

    public IReadOnlyList<ApiClientUpdateItem> Updates
    {
        get => _updates;
        private set => SetField(ref _updates, value);
    }

    public IReadOnlyList<StoreUpdateAssetRow> UpdateRows
    {
        get => _updateRows;
        private set
        {
            if (SetField(ref _updateRows, value))
            {
                OnPropertyChanged(nameof(IsUpdateRowsVisible));
                OnPropertyChanged(nameof(IsUpdateEmptyVisible));
            }
        }
    }

    public IReadOnlyList<InstalledAssetRecord> InstalledAssets
    {
        get => _installedAssets;
        private set
        {
            if (!SetField(ref _installedAssets, value))
            {
                return;
            }

            InstalledAssetRows = value.Select(StoreInstalledAssetRow.FromRecord).ToList();
            RefreshMarketRows();
        }
    }

    public IReadOnlyList<StoreInstalledAssetRow> InstalledAssetRows
    {
        get => _installedAssetRows;
        private set
        {
            if (SetField(ref _installedAssetRows, value))
            {
                OnPropertyChanged(nameof(IsInstalledRowsVisible));
                OnPropertyChanged(nameof(IsInstalledEmptyVisible));
            }
        }
    }

    public string BaseUrl => baseUrlProvider.GetBaseUrl();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await packageInstallService.CleanupStaleStagingAsync(cancellationToken);
            await RefreshSessionAsync(cancellationToken);

            InstalledAssets = await installedAssetStore.LoadAsync(cancellationToken);
            var marketResult = await RefreshAssetsCoreAsync(cancellationToken);
            await RefreshSignedInStateAfterMarketAsync(marketResult, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        var session = await accountStore.LoadAsync(cancellationToken);
        if (session is not null &&
            !string.Equals(
                StorePlatformUrlHelper.NormalizeApiBaseUrl(session.BaseUrl),
                BaseUrl.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase))
        {
            await accountStore.ClearAsync(cancellationToken);
            session = null;
            ClearAccountScopedState();
            StatusText = "运营平台地址已切换，请重新登录。";
        }

        if (session is null)
        {
            ClearAccountScopedState();
        }

        AccountText = session is null
            ? "未登录"
            : string.IsNullOrWhiteSpace(session.Account.NickName) ? session.Account.LoginUserName : session.Account.NickName;
        IsSignedIn = session is not null;
    }

    public async Task MarkSessionExpiredAsync(CancellationToken cancellationToken = default)
    {
        await accountStore.ClearAsync(cancellationToken);
        AccountText = "登录已过期";
        IsSignedIn = false;
        ClearAccountScopedState();
        StatusText = "平台登录已过期，请重新登录。";
    }

    public async Task RefreshAssetsAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await RefreshAssetsCoreAsync(resetPaging: true, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshStoreAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await RefreshSessionAsync(cancellationToken);
            InstalledAssets = await installedAssetStore.LoadAsync(cancellationToken);
            var marketResult = await RefreshAssetsCoreAsync(cancellationToken);
            await RefreshSignedInStateAfterMarketAsync(marketResult, cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<StoreOperationResult> RefreshAssetsCoreAsync(CancellationToken cancellationToken = default)
        => await RefreshAssetsCoreAsync(resetPaging: true, cancellationToken);

    private async Task<StoreOperationResult> RefreshAssetsCoreAsync(bool resetPaging, CancellationToken cancellationToken = default)
    {
        try
        {
            var nextPage = resetPaging ? 1 : _marketCurrentPage + 1;
            var pageResult = await marketClient.GetAssetsAsync(nextPage, MarketPageSize, cancellationToken: cancellationToken);

            Assets = resetPaging
                ? pageResult.Items
                : Assets.Concat(pageResult.Items).ToList();

            _marketCurrentPage = pageResult.Page;
            _marketTotalCount = pageResult.TotalCount;
            HasMoreMarketAssets = pageResult.HasNextPage;
            RefreshMarketRows();

            StatusText = Assets.Count == 0
                ? "资源市场暂无可展示资源。"
                : HasMoreMarketAssets
                    ? $"已加载 {Assets.Count}/{_marketTotalCount} 个资源。"
                    : $"已加载全部 {Assets.Count} 个资源。";

            return StoreOperationResult.Success();
        }
        catch (Exception ex)
        {
            ClearMarketState();
            StatusText = $"资源市场加载失败：{ex.Message}";
            return StoreOperationResult.Failed(ex.Message);
        }
    }

    public async Task LoadMoreAssetsAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy || IsLoadingMoreAssets || !HasMoreMarketAssets)
        {
            return;
        }

        IsLoadingMoreAssets = true;
        try
        {
            StatusText = "正在加载更多资源...";
            var result = await RefreshAssetsCoreAsync(resetPaging: false, cancellationToken);
            if (!result.Succeeded)
            {
                StatusText = $"加载下一页失败：{result.Message}";
            }
        }
        finally
        {
            IsLoadingMoreAssets = false;
        }
    }

    private async Task RefreshSignedInStateAfterMarketAsync(StoreOperationResult marketResult, CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn)
        {
            return;
        }

        await TrySyncCoreAsync(cancellationToken);
        if (!marketResult.Succeeded)
        {
            StatusText = $"资源市场加载失败：{marketResult.Message}；{StatusText}";
        }
    }

    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await TrySyncCoreAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task TrySyncCoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SyncCoreAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusText = $"同步失败：{ex.Message}";
        }
    }

    private async Task SyncCoreAsync(CancellationToken cancellationToken = default)
    {
        var sync = await marketClient.SyncAsync(cancellationToken);
        AccountText = string.IsNullOrWhiteSpace(sync.Account.NickName) ? sync.Account.LoginUserName : sync.Account.NickName;
        IsSignedIn = true;
        OwnedAssets = sync.Assets;
        OwnedAssetRows = sync.Assets.Select(StoreInstallAssetRow.FromManifest).ToList();
        var updateResult = await RefreshUpdatesCoreAsync(cancellationToken);
        StatusText = updateResult.Succeeded
            ? $"同步完成，当前账号拥有 {sync.Assets.Count} 个资源，{Updates.Count} 个可更新。"
            : $"资源同步完成，当前账号拥有 {sync.Assets.Count} 个资源；检查更新失败：{updateResult.Message}";
    }

    public async Task RefreshUpdatesAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var result = await RefreshUpdatesCoreAsync(cancellationToken);
            if (!result.Succeeded)
            {
                StatusText = $"检查更新失败：{result.Message}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<StoreOperationResult> RefreshUpdatesCoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            InstalledAssets = await installedAssetStore.LoadAsync(cancellationToken);
            var installed = InstalledAssets
                .Select(static asset => new ApiClientInstalledAsset(
                    asset.AssetId,
                    asset.AssetSlug,
                    asset.VersionId,
                    asset.VersionName,
                    asset.PackageHash))
                .ToList();

            var response = await marketClient.CheckUpdatesAsync(installed, cancellationToken);
            Updates = response.Updates;
            UpdateRows = response.Updates.Select(StoreUpdateAssetRow.FromUpdate).ToList();
            return StoreOperationResult.Success();
        }
        catch (Exception ex)
        {
            ClearUpdateState();
            return StoreOperationResult.Failed(ex.Message);
        }
    }

    public async Task<StoreInstallManifest?> GetInstallManifestAsync(StoreAssetItem asset, CancellationToken cancellationToken = default)
    {
        if (!IsSignedIn)
        {
            StatusText = "需要登录平台账号后才能安装资源。";
            return null;
        }

        try
        {
            StatusText = $"正在获取 {asset.Name} 的安装清单...";
            var assetIdentifier = string.IsNullOrWhiteSpace(asset.Id) ? asset.Slug : asset.Id;
            return await marketClient.GetInstallManifestAsync(assetIdentifier, cancellationToken);
        }
        catch (Exception ex)
        {
            StatusText = $"获取安装清单失败：{ex.Message}";
            return null;
        }
    }

    public async Task InstallAsync(StoreInstallManifest manifest, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            StatusText = $"正在安装 {manifest.AssetName} {manifest.Version.VersionName}...";

            var result = await packageInstallService.InstallAsync(manifest, cancellationToken);
            if (!result.Succeeded)
            {
                StatusText = $"安装失败：{result.Message}";
                return;
            }

            InstalledAssets = await installedAssetStore.LoadAsync(cancellationToken);
            var updateResult = await RefreshUpdatesCoreAsync(cancellationToken);
            StatusText = updateResult.Succeeded
                ? $"{manifest.AssetName} 安装完成。"
                : $"{manifest.AssetName} 安装完成；检查更新失败：{updateResult.Message}";
        }
        catch (Exception ex)
        {
            StatusText = $"安装失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UninstallAsync(InstalledAssetRecord asset, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            StatusText = $"正在卸载 {asset.AssetName} {asset.VersionName}...";

            var result = await packageInstallService.UninstallAsync(asset, cancellationToken);
            if (!result.Succeeded)
            {
                StatusText = $"卸载失败：{result.Message}";
                return;
            }

            InstalledAssets = await installedAssetStore.LoadAsync(cancellationToken);
            if (IsSignedIn)
            {
                var updateResult = await RefreshUpdatesCoreAsync(cancellationToken);
                StatusText = updateResult.Succeeded
                    ? $"{asset.AssetName} 卸载完成。"
                    : $"{asset.AssetName} 卸载完成；检查更新失败：{updateResult.Message}";
            }
            else
            {
                StatusText = $"{asset.AssetName} 卸载完成。";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"卸载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await accountStore.ClearAsync(cancellationToken);
        AccountText = "未登录";
        IsSignedIn = false;
        ClearAccountScopedState();
        StatusText = "已退出平台账号。";
    }

    public void ReportStatus(string message)
        => StatusText = message;

    public async Task ToggleMarketAssetExpandedAsync(StoreMarketAssetRow row, CancellationToken cancellationToken = default)
    {
        if (row.IsExpanded)
        {
            row.SetExpanded(false);
            return;
        }

        row.SetExpanded(true);
        await EnsureMarketAssetDetailAsync(row, cancellationToken);
    }

    public async Task EnsureMarketAssetDetailAsync(StoreMarketAssetRow row, CancellationToken cancellationToken = default)
    {
        if (row.HasLoadedDetail || row.IsLoadingDetail)
        {
            return;
        }

        row.SetLoadingDetail(true);
        try
        {
            var detail = await marketClient.GetAssetDetailAsync(row.Source.Slug, cancellationToken);
            var detailText = BuildMarketAssetDetailText(detail);
            row.ApplyDetail(detailText);
        }
        catch (Exception ex)
        {
            row.ApplyDetailError($"加载详情失败：{ex.Message}");
        }
        finally
        {
            row.SetLoadingDetail(false);
        }
    }

    private void ClearAccountScopedState()
    {
        OwnedAssets = [];
        OwnedAssetRows = [];
        ClearUpdateState();
    }

    private void ClearMarketState()
    {
        _marketCurrentPage = 0;
        _marketTotalCount = 0;
        HasMoreMarketAssets = false;
        Assets = [];
        MarketAssetRows = [];
    }

    private void ClearUpdateState()
    {
        Updates = [];
        UpdateRows = [];
    }

    public void SetMarketTypeFilter(AssetType? assetType)
    {
        if (_marketTypeFilter == assetType)
        {
            return;
        }

        _marketTypeFilter = assetType;
        OnMarketFilterStateChanged();
        RefreshMarketRows();
    }

    public void ShowPopularAssets()
    {
        if (_isPopularSortActive)
        {
            return;
        }

        _isPopularSortActive = true;
        OnPropertyChanged(nameof(IsPopularSortActive));
        RefreshMarketRows();
        StatusText = "资源市场已按推荐与下载热度排序。";
    }

    public void ShowAllAssets()
    {
        var changed = !string.IsNullOrWhiteSpace(_searchText)
            || _selectedCategoryFilter != AllCategoryFilterText
            || _marketTypeFilter is not null
            || _isPopularSortActive;
        if (!changed)
        {
            return;
        }

        _searchText = string.Empty;
        SelectedCategoryFilter = AllCategoryFilterText;
        _marketTypeFilter = null;
        _isPopularSortActive = false;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(IsPopularSortActive));
        OnMarketFilterStateChanged();
        RefreshMarketRows();
        StatusText = "已显示全部资源。";
    }

    private void RefreshMarketRows()
    {
        var searchText = SearchText.Trim();
        var query = Assets
            .Where(asset => _marketTypeFilter is null || asset.Type == _marketTypeFilter)
            .Where(asset => IsAllCategorySelected() || string.Equals(asset.CategoryName, _selectedCategoryFilter, StringComparison.OrdinalIgnoreCase))
            .Where(asset => string.IsNullOrWhiteSpace(searchText) || MatchesSearch(asset, searchText));

        if (_isPopularSortActive)
        {
            query = query
                .OrderByDescending(asset => asset.IsFeatured)
                .ThenByDescending(asset => asset.DownloadCount)
                .ThenBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase);
        }

        MarketAssetRows = query
            .Select(asset => StoreMarketAssetRow.FromAsset(asset, InstalledAssets, IsSignedIn))
            .ToList();
    }

    private void OnMarketFilterStateChanged()
    {
        OnPropertyChanged(nameof(IsAllMarketFilterActive));
        OnPropertyChanged(nameof(IsPluginMarketFilterActive));
        OnPropertyChanged(nameof(IsSkillMarketFilterActive));
        OnPropertyChanged(nameof(IsAgentMarketFilterActive));
        OnPropertyChanged(nameof(IsSolutionMarketFilterActive));
    }

    private void RefreshCategoryFilterOptions()
    {
        var options = Assets
            .Select(static asset => asset.CategoryName?.Trim())
            .Where(static category => !string.IsNullOrWhiteSpace(category))
            .Select(static category => category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static category => category, StringComparer.OrdinalIgnoreCase)
            .Prepend(AllCategoryFilterText)
            .ToList();

        CategoryFilterOptions = options;
        if (!options.Contains(_selectedCategoryFilter, StringComparer.OrdinalIgnoreCase))
        {
            _selectedCategoryFilter = AllCategoryFilterText;
            OnPropertyChanged(nameof(SelectedCategoryFilter));
        }
    }

    private bool IsAllCategorySelected()
        => string.Equals(_selectedCategoryFilter, AllCategoryFilterText, StringComparison.OrdinalIgnoreCase);

    private void OnListStateChanged()
    {
        OnPropertyChanged(nameof(IsMarketRowsVisible));
        OnPropertyChanged(nameof(IsMarketEmptyVisible));
        OnPropertyChanged(nameof(MarketEmptyText));
        OnPropertyChanged(nameof(IsOwnedRowsVisible));
        OnPropertyChanged(nameof(IsOwnedEmptyVisible));
        OnPropertyChanged(nameof(OwnedEmptyText));
        OnPropertyChanged(nameof(IsUpdateRowsVisible));
        OnPropertyChanged(nameof(IsUpdateEmptyVisible));
        OnPropertyChanged(nameof(UpdateEmptyText));
        OnPropertyChanged(nameof(IsInstalledRowsVisible));
        OnPropertyChanged(nameof(IsInstalledEmptyVisible));
        OnPropertyChanged(nameof(InstalledEmptyText));
    }

    private static bool MatchesSearch(StoreAssetItem asset, string searchText)
    {
        var typeText = ToDisplayAssetType(asset.Type);
        return Contains(asset.Name, searchText)
            || Contains(asset.ShortDescription, searchText)
            || Contains(asset.DeveloperName, searchText)
            || Contains(asset.CategoryName, searchText)
            || Contains(typeText, searchText);
    }

    private static bool Contains(string? value, string searchText)
        => !string.IsNullOrWhiteSpace(value)
            && value.Contains(searchText, StringComparison.OrdinalIgnoreCase);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    internal static string ToDisplayAssetType(AssetType assetType)
        => assetType switch
        {
            AssetType.Plugin => "插件",
            AssetType.Skill => "技能",
            AssetType.Agent => "智能体",
            AssetType.Solution => "方案",
            _ => "资源"
        };

    internal static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    private static string BuildMarketAssetDetailText(StoreAssetDetail detail)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(detail.Description))
        {
            parts.Add(detail.Description.Trim());
        }

        if (!string.IsNullOrWhiteSpace(detail.CurrentVersion?.ReleaseNotes))
        {
            parts.Add($"版本说明：{detail.CurrentVersion.ReleaseNotes.Trim()}");
        }

        var solutionResourceSummary = BuildSolutionResourceSummary(detail.Type, detail.CurrentVersion?.Manifest);
        if (!string.IsNullOrWhiteSpace(solutionResourceSummary))
        {
            parts.Add($"方案内容：{Environment.NewLine}{solutionResourceSummary}");
        }

        if (!string.IsNullOrWhiteSpace(detail.Tags))
        {
            parts.Add($"功能标签：{detail.Tags.Trim()}");
        }

        if (parts.Count == 0)
        {
            parts.Add("当前资源暂未提供更多详情。");
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    internal static string? BuildSolutionResourceSummary(AssetType assetType, JsonElement? manifest)
    {
        if (assetType != AssetType.Solution || manifest is null)
        {
            return null;
        }

        SolutionPackageManifest? solutionManifest;
        try
        {
            solutionManifest = manifest.Value.Deserialize(StoreJsonContext.Default.SolutionPackageManifest);
        }
        catch (JsonException)
        {
            return "包内资源清单解析失败。";
        }

        if (solutionManifest?.Assets is null)
        {
            return "包内资源清单未提供。";
        }

        var lines = new List<string>();
        AddSolutionAssetLine(lines, "插件", solutionManifest.Assets.Plugins);
        AddSolutionAssetLine(lines, "技能", solutionManifest.Assets.Skills);
        AddSolutionAssetLine(lines, "智能体", solutionManifest.Assets.Agents);

        return lines.Count == 0
            ? "包内资源清单为空。"
            : string.Join(Environment.NewLine, lines);
    }

    internal static string BuildSolutionUpdateDescription(ApiClientUpdateItem update)
    {
        var releaseNotes = update.Current.Version.ReleaseNotes.Trim();
        if (update.Current.AssetType != AssetType.Solution)
        {
            return releaseNotes;
        }

        var resourceSummary = BuildSolutionResourceSummary(update.Current.AssetType, update.Current.Manifest);
        if (string.IsNullOrWhiteSpace(resourceSummary))
        {
            return string.IsNullOrWhiteSpace(releaseNotes)
                ? "解决方案更新会同步包内子资源。"
                : $"{releaseNotes}；解决方案更新会同步包内子资源。";
        }

        return string.IsNullOrWhiteSpace(releaseNotes)
            ? $"解决方案更新会同步包内子资源：{FlattenSummary(resourceSummary)}"
            : $"{releaseNotes}；解决方案更新会同步包内子资源：{FlattenSummary(resourceSummary)}";
    }

    private static void AddSolutionAssetLine(List<string> lines, string typeText, IReadOnlyList<SolutionPackageAsset>? assets)
    {
        if (assets is null || assets.Count == 0)
        {
            return;
        }

        var names = assets
            .Where(static asset => !string.IsNullOrWhiteSpace(asset.Slug))
            .Select(static asset => string.IsNullOrWhiteSpace(asset.Version)
                ? asset.Slug
                : $"{asset.Slug} ({asset.Version})")
            .ToList();

        if (names.Count > 0)
        {
            lines.Add($"{typeText}：{string.Join("、", names)}");
        }
    }

    private static string FlattenSummary(string summary)
        => summary.Replace(Environment.NewLine, "；", StringComparison.Ordinal);
}

public sealed class StoreMarketAssetRow : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isLoadingDetail;
    private bool _hasLoadedDetail;
    private string _detailText;
    private string? _detailErrorText;

    private StoreMarketAssetRow(
        StoreAssetItem source,
        string name,
        string description,
        string assetTypeText,
        string developerName,
        string versionText,
        string priceText,
        string stateText,
        string actionText,
        bool canInstall,
        string installTip)
    {
        Source = source;
        Name = name;
        Summary = description;
        AssetTypeText = assetTypeText;
        DeveloperName = developerName;
        VersionText = versionText;
        PriceText = priceText;
        StateText = stateText;
        ActionText = actionText;
        CanInstall = canInstall;
        InstallTip = installTip;
        _detailText = description;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StoreAssetItem Source { get; }

    public string Name { get; }

    public string Summary { get; }

    public string AssetTypeText { get; }

    public string DeveloperName { get; }

    public string VersionText { get; }

    public string PriceText { get; }

    public string StateText { get; }

    public string ActionText { get; }

    public bool CanInstall { get; }

    public string InstallTip { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (SetField(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ExpandButtonText));
                OnPropertyChanged(nameof(IsDetailVisible));
            }
        }
    }

    public bool IsLoadingDetail
    {
        get => _isLoadingDetail;
        private set
        {
            if (SetField(ref _isLoadingDetail, value))
            {
                OnPropertyChanged(nameof(DetailToolTipText));
            }
        }
    }

    public bool HasLoadedDetail
    {
        get => _hasLoadedDetail;
        private set => SetField(ref _hasLoadedDetail, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set
        {
            if (SetField(ref _detailText, value))
            {
                OnPropertyChanged(nameof(DetailToolTipText));
            }
        }
    }

    public string? DetailErrorText
    {
        get => _detailErrorText;
        private set
        {
            if (SetField(ref _detailErrorText, value))
            {
                OnPropertyChanged(nameof(IsDetailErrorVisible));
                OnPropertyChanged(nameof(DetailToolTipText));
            }
        }
    }

    public string DetailToolTipText
    {
        get
        {
            if (IsLoadingDetail)
            {
                return "正在加载详情...";
            }

            return string.IsNullOrWhiteSpace(DetailErrorText) ? DetailText : DetailErrorText;
        }
    }

    public bool IsDetailVisible => IsExpanded;

    public bool IsDetailErrorVisible => !string.IsNullOrWhiteSpace(DetailErrorText);

    public bool IsDetailTextVisible => !IsLoadingDetail && !IsDetailErrorVisible;

    public string ExpandButtonText => IsExpanded ? "收起详情" : "查看详情";

    public static StoreMarketAssetRow FromAsset(StoreAssetItem asset, IReadOnlyList<InstalledAssetRecord> installedAssets, bool isSignedIn)
    {
        var installed = installedAssets.FirstOrDefault(x =>
            string.Equals(x.AssetId, asset.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.AssetSlug, asset.Slug, StringComparison.OrdinalIgnoreCase));

        var stateText = installed is null ? "未安装" : $"已安装 {installed.VersionName}";
        var actionText = installed is null ? "安装" : "重装";
        return new StoreMarketAssetRow(
            asset,
            asset.Name,
            asset.ShortDescription,
            StoreWindowViewModel.ToDisplayAssetType(asset.Type),
            asset.DeveloperName,
            string.IsNullOrWhiteSpace(asset.CurrentVersionName) ? "当前版本" : asset.CurrentVersionName,
            asset.MinPrice is null || asset.HasFreePlan ? "免费" : $"{asset.MinPrice:0.##}",
            stateText,
            actionText,
            isSignedIn,
            isSignedIn ? $"{actionText} {asset.Name}" : "登录平台账号后安装资源");
    }

    public void SetExpanded(bool isExpanded)
        => IsExpanded = isExpanded;

    public void SetLoadingDetail(bool isLoading)
        => IsLoadingDetail = isLoading;

    public void ApplyDetail(string detailText)
    {
        DetailText = detailText;
        DetailErrorText = null;
        HasLoadedDetail = true;
    }

    public void ApplyDetailError(string errorText)
    {
        DetailErrorText = errorText;
        HasLoadedDetail = false;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record StoreOperationResult(bool Succeeded, string Message)
{
    public static StoreOperationResult Success()
        => new(true, string.Empty);

    public static StoreOperationResult Failed(string message)
        => new(false, message);
}

public sealed record StoreInstallAssetRow(
    StoreInstallManifest Source,
    string Name,
    string Description,
    string AssetTypeText,
    string DeveloperName,
    string VersionText,
    string SizeText,
    string StateText)
{
    public static StoreInstallAssetRow FromManifest(StoreInstallManifest manifest)
        => new(
            manifest,
            manifest.AssetName,
            manifest.Version.ReleaseNotes,
            StoreWindowViewModel.ToDisplayAssetType(manifest.AssetType),
            manifest.DeveloperName,
            manifest.Version.VersionName,
            StoreWindowViewModel.FormatSize(manifest.Version.PackageSize),
            "可安装");
}

public sealed record StoreUpdateAssetRow(
    ApiClientUpdateItem Source,
    string Name,
    string Description,
    string AssetTypeText,
    string DeveloperName,
    string InstalledVersionText,
    string CurrentVersionText,
    string SizeText,
    string StateText)
{
    public static StoreUpdateAssetRow FromUpdate(ApiClientUpdateItem update)
        => new(
            update,
            update.Current.AssetName,
            StoreWindowViewModel.BuildSolutionUpdateDescription(update),
            StoreWindowViewModel.ToDisplayAssetType(update.Current.AssetType),
            update.Current.DeveloperName,
            update.InstalledVersionName ?? "-",
            update.Current.Version.VersionName,
            StoreWindowViewModel.FormatSize(update.Current.Version.PackageSize),
            update.Current.AssetType == AssetType.Solution ? "方案更新" : "单资源更新");
}

public sealed record StoreInstalledAssetRow(
    InstalledAssetRecord Source,
    string Name,
    string AssetTypeText,
    string VersionText,
    string SizeText,
    string InstalledDirectory,
    string StateText)
{
    public static StoreInstalledAssetRow FromRecord(InstalledAssetRecord record)
        => new(
            record,
            record.AssetName,
            StoreWindowViewModel.ToDisplayAssetType(record.AssetType),
            record.VersionName,
            StoreWindowViewModel.FormatSize(record.PackageSize),
            record.InstalledDirectory,
            "已安装");
}
