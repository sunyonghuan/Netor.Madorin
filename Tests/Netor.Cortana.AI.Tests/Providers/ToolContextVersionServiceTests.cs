using Netor.Cortana.AI.Providers;

namespace Netor.Cortana.AI.Tests.Providers;

[TestClass]
public sealed class ToolContextVersionServiceTests
{
    [TestMethod]
    public void Current_NewService_ReturnsZero()
    {
        var service = new ToolContextVersionService();

        Assert.AreEqual(0, service.Current);
    }

    [TestMethod]
    public void Bump_WhenCalled_IncrementsVersion()
    {
        var service = new ToolContextVersionService();

        var first = service.Bump();
        var second = service.Bump();

        Assert.AreEqual(1, first);
        Assert.AreEqual(2, second);
        Assert.AreEqual(2, service.Current);
    }

    [TestMethod]
    public void Bump_WhenCalledInParallel_IncrementsAtomically()
    {
        var service = new ToolContextVersionService();

        Parallel.For(0, 1000, _ => service.Bump());

        Assert.AreEqual(1000, service.Current);
    }
}
