using DesktopPet.Ai;
using System.Text.Json;

namespace DesktopPet.Ai.Tests;

[TestClass]
public sealed class PetMcpServerTests
{
    [TestMethod]
    public void HandleRequest_Initialize_ReturnsServerCapabilities()
    {
        var server = new PetMcpServer();

        using var response = server.HandleRequest("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}
            """);

        Assert.AreEqual("2025-11-25", response.RootElement.GetProperty("protocolVersion").GetString());
        Assert.AreEqual("DesktopPet", response.RootElement.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.IsTrue(response.RootElement.GetProperty("capabilities").TryGetProperty("tools", out _));
    }

    [TestMethod]
    public void HandleRequest_ToolsList_ReturnsPetTools()
    {
        var server = new PetMcpServer();

        using var response = server.HandleRequest("""
            {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
            """);

        var tools = response.RootElement.GetProperty("tools");
        Assert.AreEqual(5, tools.GetArrayLength());
        Assert.AreEqual("pet.show", tools[0].GetProperty("name").GetString());
        Assert.AreEqual("pet.status", tools[4].GetProperty("name").GetString());
    }

    [TestMethod]
    public void HandleRequest_PetSay_UpdatesStatusText()
    {
        var server = new PetMcpServer();

        using var response = server.HandleRequest("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"pet.say","arguments":{"text":"你好"}}}
            """);

        using var status = JsonDocument.Parse(response.RootElement.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.AreEqual("Speaking", status.RootElement.GetProperty("state").GetString());
        Assert.AreEqual("你好", status.RootElement.GetProperty("subtitle").GetString());
        Assert.IsTrue(status.RootElement.GetProperty("isSpeaking").GetBoolean());
    }

    [TestMethod]
    public void HandleRequest_UnknownTool_ReturnsInvalidParamsError()
    {
        var server = new PetMcpServer();

        using var response = server.HandleRequest("""
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"pet.missing","arguments":{}}}
            """);

        var error = response.RootElement.GetProperty("error");
        Assert.AreEqual(-32602, error.GetProperty("code").GetInt32());
    }
}
