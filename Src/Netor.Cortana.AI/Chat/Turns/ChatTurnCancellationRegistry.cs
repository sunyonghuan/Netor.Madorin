namespace Netor.Cortana.AI;

/// <summary>
/// 管理当前活跃聊天 turn 的登记、查询、取消与清理。
/// 阶段 3.5 仅维护单活跃 turn，为后续 ChatTurnExecutor 抽离提供统一状态入口。
/// </summary>
public sealed class ChatTurnCancellationRegistry
{
    private readonly Lock _gate = new();
    private ChatTurnContext? _currentTurn;

    public bool HasActiveTurn()
    {
        lock (_gate)
        {
            return _currentTurn is not null;
        }
    }

    public ChatTurnContext? GetCurrentTurn()
    {
        lock (_gate)
        {
            return _currentTurn;
        }
    }

    public void Register(ChatTurnContext turnContext)
    {
        ArgumentNullException.ThrowIfNull(turnContext);

        lock (_gate)
        {
            _currentTurn = turnContext;
        }
    }

    public ChatTurnContext? CancelCurrentTurn()
    {
        var turnContext = GetCurrentTurn();
        turnContext?.Cancel();
        return turnContext;
    }

    public ChatTurnContext? ClearCurrentTurn(string turnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        lock (_gate)
        {
            if (!string.Equals(_currentTurn?.TurnId, turnId, StringComparison.Ordinal))
            {
                return null;
            }

            var turnContext = _currentTurn;
            _currentTurn = null;
            return turnContext;
        }
    }

    public ChatTurnContext? ForceClearCurrentTurn()
    {
        lock (_gate)
        {
            var turnContext = _currentTurn;
            _currentTurn = null;
            return turnContext;
        }
    }
}
