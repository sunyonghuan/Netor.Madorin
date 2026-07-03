namespace Netor.Cortana.UI;

internal sealed class NonExpertSuppressingRealtimeOutput(
    UiChatOutputChannel expertChannel) : IRealtimeProcessOutput
{
    public Task OnProcessEventAsync(RealtimeProcessEvent evt, CancellationToken ct = default)
    {
        if (NonExpertProcessScope.Active)
        {
            return Task.CompletedTask;
        }

        return expertChannel.OnProcessEventAsync(evt, ct);
    }
}
