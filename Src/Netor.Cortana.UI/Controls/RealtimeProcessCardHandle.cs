namespace Netor.Cortana.UI.Controls;

public sealed class RealtimeProcessCardHandle(RealtimeProcessCard card)
{
    public string ProcessId { get; } = card.ProcessId;

    public void UpdateStatus(string status, int? exitCode, long durationMs)
    {
        card.UpdateStatus(status, exitCode, durationMs);
    }

    public void AppendContent(string content)
    {
        card.AppendContent(content);
    }

    public void Complete(string status, int? exitCode, long durationMs)
    {
        card.Complete(status, exitCode, durationMs);
    }
}
