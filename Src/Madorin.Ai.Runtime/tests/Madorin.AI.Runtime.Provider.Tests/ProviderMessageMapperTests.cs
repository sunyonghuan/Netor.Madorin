using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Anthropic.Protocol;
using Madorin.AI.Runtime.Providers.OpenAI.Protocol;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ProviderMessageMapperTests
{
    [TestMethod]
    public void MessageMappers_PreserveRolesAndMessageBoundaries()
    {
        RuntimeProviderMessage[] source =
        [
            new(RuntimeProviderRoles.System, [new TextContentBlock("instructions")]),
            new(RuntimeProviderRoles.User, [new TextContentBlock("question")]),
            new(
                RuntimeProviderRoles.Assistant,
                [new TextContentBlock("answer"), new ReasoningContentBlock("reasoning")])
        ];

        AssertMessages(OpenAIMessageMapper.ToMessages(source));
        AssertMessages(AnthropicMessageMapper.ToMessages(source));
    }

    private static void AssertMessages(ChatMessage[] messages)
    {
        Assert.HasCount(3, messages);
        Assert.AreEqual(ChatRole.System, messages[0].Role);
        Assert.AreEqual(ChatRole.User, messages[1].Role);
        Assert.AreEqual(ChatRole.Assistant, messages[2].Role);
        Assert.HasCount(2, messages[2].Contents);
    }
}
