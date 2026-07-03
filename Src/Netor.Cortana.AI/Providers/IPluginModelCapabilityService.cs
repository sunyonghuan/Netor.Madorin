using Netor.Cortana.Entitys.ModelCapability;

namespace Netor.Cortana.AI.Providers;

public interface IPluginModelCapabilityService
{
    Task<ModelCapabilityResponse> InvokeAsync(ModelCapabilityRequest request, CancellationToken cancellationToken = default);
}
