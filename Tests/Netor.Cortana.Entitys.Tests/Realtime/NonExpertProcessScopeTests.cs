using Netor.Cortana.Entitys;

namespace Netor.Cortana.Entitys.Tests.Realtime;

[TestClass]
public sealed class NonExpertProcessScopeTests
{
    [TestMethod]
    public void Enter_ActivatesScope()
    {
        Assert.IsFalse(NonExpertProcessScope.Active);

        using var _ = NonExpertProcessScope.Enter();

        Assert.IsTrue(NonExpertProcessScope.Active);
    }

    [TestMethod]
    public void Dispose_RestoresPrevious()
    {
        using (NonExpertProcessScope.Enter())
        {
            Assert.IsTrue(NonExpertProcessScope.Active);
        }

        Assert.IsFalse(NonExpertProcessScope.Active);
    }

    [TestMethod]
    public void Enter_NestedRestoresPrevious()
    {
        using (NonExpertProcessScope.Enter())
        {
            using (NonExpertProcessScope.Enter())
            {
                Assert.IsTrue(NonExpertProcessScope.Active);
            }

            Assert.IsTrue(NonExpertProcessScope.Active);
        }

        Assert.IsFalse(NonExpertProcessScope.Active);
    }

    [TestMethod]
    public async Task Enter_FlowsThroughTaskRun()
    {
        using var _ = NonExpertProcessScope.Enter();

        var observed = await Task.Run(static () => NonExpertProcessScope.Active);

        Assert.IsTrue(observed);
    }

    [TestMethod]
    public async Task Enter_DoesNotLeakAcrossParallelTasks()
    {
        var task1 = Task.Run(async () =>
        {
            using var _ = NonExpertProcessScope.Enter();
            await Task.Delay(50);
            return NonExpertProcessScope.Active;
        });

        var task2 = Task.Run(async () =>
        {
            await Task.Delay(50);
            return NonExpertProcessScope.Active;
        });

        var results = await Task.WhenAll(task1, task2);

        Assert.IsTrue(results[0]);
        Assert.IsFalse(results[1]);
    }
}
