namespace Netor.Cortana.Store.Abstractions;

public interface IPluginActivationService
{
    void Unload(string directoryName);

    Task<bool> LoadByPathAsync(string pluginPath, CancellationToken cancellationToken = default);
}
