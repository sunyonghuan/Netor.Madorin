using Netor.Madorin.Plugin;
using Netor.Cortana.Store.Abstractions;

namespace Netor.Cortana.UI;

internal sealed class StorePluginActivationService(PluginLoader pluginLoader) : IPluginActivationService
{
    public void Unload(string directoryName)
        => pluginLoader.UnloadPlugin(directoryName);

    public Task<bool> LoadByPathAsync(string pluginPath, CancellationToken cancellationToken = default)
        => pluginLoader.LoadPluginByPathAsync(pluginPath, cancellationToken);
}
