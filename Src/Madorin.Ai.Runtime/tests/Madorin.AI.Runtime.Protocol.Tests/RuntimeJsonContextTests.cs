using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class RuntimeJsonContextTests
{
    [TestMethod]
    public void RuntimeVersionInfo_RoundTripsWithSourceGeneratedMetadata()
    {
        var expected = new RuntimeVersionInfo(
            "Madorin.AI.Runtime",
            "0.1.0",
            ProtocolVersions.Current,
            ".NET 10.0",
            "Windows",
            "x64");

        var json = JsonSerializer.Serialize(
            expected,
            RuntimeJsonContext.Default.RuntimeVersionInfo);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.RuntimeVersionInfo);

        Assert.AreEqual(expected, actual);
        StringAssert.Contains(json, "\"protocolVersion\":\"1.2\"");
    }
}
