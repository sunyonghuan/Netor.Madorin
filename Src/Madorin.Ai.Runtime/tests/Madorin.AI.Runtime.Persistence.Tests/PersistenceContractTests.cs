using Madorin.AI.Runtime.Persistence.Abstractions;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class PersistenceContractTests
{
    [TestMethod]
    public void IConversationStore_ExposesAppendAndReadOperations()
    {
        var methodNames = typeof(IConversationStore)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { nameof(IConversationStore.AppendAsync), nameof(IConversationStore.ReadAsync) },
            methodNames);
    }
}
