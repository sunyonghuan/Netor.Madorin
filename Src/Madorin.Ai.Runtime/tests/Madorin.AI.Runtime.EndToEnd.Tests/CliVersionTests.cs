using System.Text.Json;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class CliVersionTests
{
    [TestMethod]
    public void Run_VersionJson_WritesMachineReadableVersion()
    {
        using var output = new StringWriter();

        var exitCode = CliApplication.Run(["version", "--json"], output);

        Assert.AreEqual(ExitCodes.Success, exitCode);
        var json = output.ToString();
        StringAssert.Contains(json, "\"protocolVersion\":\"1.1\"");
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(
            ProtocolVersions.Current,
            document.RootElement.GetProperty("protocolVersion").GetString());
    }
}
