using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.Hitl.Json;
using Netor.Cortana.AI.Hitl.Models;

namespace Netor.Cortana.AI.Hitl;

/// <summary>
/// HITL 工具：向用户提问并挂起当前执行上下文。
/// </summary>
public sealed class HitlTools
{
    private readonly IHitlContext _context;
    private readonly IHitlNotifier _notifier;

    public HitlTools(IHitlContext context, IHitlNotifier notifier)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
    }

    public AIFunction CreateAskUserTool()
    {
        [Description("需要用户决策或补充关键信息时调用。调用后系统会把问题展示给用户，用户回复后继续任务。")]
        async Task<string> AskUserAsync(
            [Description("要询问用户的问题，必须简短明确")] string question,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(question))
            {
                return "错误：question 不能为空";
            }

            var trimmedQuestion = question.Trim();
            var requestId = Guid.NewGuid().ToString("N");
            var snapshot = new HitlPendingRequestSnapshot(
                requestId,
                "ask_user",
                null,
                null,
                null,
                trimmedQuestion);

            _context.SetPending(
                requestId,
                "ask_user",
                JsonSerializer.Serialize(snapshot, HitlJsonContext.Default.HitlPendingRequestSnapshot));

            await _notifier.PublishAskUserAsync(
                _context.ContextId,
                requestId,
                trimmedQuestion,
                ct);

            return "已向用户提问。请等待用户回复后再继续。";
        }

        return AIFunctionFactory.Create(AskUserAsync, new AIFunctionFactoryOptions
        {
            Name = "ask_user",
            Description = "向用户提出一个必须由用户决策或补充的问题。"
        });
    }
}
