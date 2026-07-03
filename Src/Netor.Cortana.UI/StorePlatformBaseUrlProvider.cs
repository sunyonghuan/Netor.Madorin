using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store;

namespace Netor.Cortana.UI;

internal sealed class StorePlatformBaseUrlProvider(SystemSettingsService settings) : IPlatformBaseUrlProvider
{
    public string GetBaseUrl()
        => StorePlatformUrlHelper.NormalizeApiBaseUrl(
            settings.GetValue(AppBranding.PlatformBaseUrlSettingKey, AppBranding.LocalPlatformApiBaseUrl));
}
