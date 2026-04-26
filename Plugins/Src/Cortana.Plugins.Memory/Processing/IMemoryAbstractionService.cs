namespace Cortana.Plugins.Memory.Processing;

public interface IMemoryAbstractionService
{
    void RunAbstractionPass(string? agentId = null, string? workspaceId = null, int minSupportCount = 3, int topPerTopic = 50);
}
