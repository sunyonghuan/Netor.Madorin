using Madorin.AI.Runtime.Cli;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class CliCommandSmokeTests
{
    [TestMethod]
    public void Run_Doctor_ReturnsSuccess()
    {
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(["doctor"], output);

        Assert.AreEqual(ExitCodes.Success, exitCode);
    }

    [TestMethod]
    public void Run_Version_ReturnsSuccess()
    {
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(["version"], output);

        Assert.AreEqual(ExitCodes.Success, exitCode);
    }

    [TestMethod]
    public void Run_ConfigValidate_ReturnsSuccess()
    {
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(["config", "validate"], output);

        Assert.AreEqual(ExitCodes.Success, exitCode);
    }
}
