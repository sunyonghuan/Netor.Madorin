using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class RuntimeModeTests
{
    private static readonly string[] ExpectedModes = ["Expert", "Meeting", "Work"];

    [TestMethod]
    public void RuntimeMode_DefinesTheThreeV1Modes()
    {
        CollectionAssert.AreEquivalent(
            ExpectedModes,
            Enum.GetNames<RuntimeMode>());
    }
}
