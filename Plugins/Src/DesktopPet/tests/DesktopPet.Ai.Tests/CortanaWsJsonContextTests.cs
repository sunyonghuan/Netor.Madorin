using System.Text.Json;

namespace DesktopPet.Ai.Tests;

[TestClass]
public sealed class CortanaWsJsonContextTests
{
    [TestMethod]
    public void Deserialize_ServerMessage_UsesSourceGeneratedContext()
    {
        var message = JsonSerializer.Deserialize(
            """{"type":"connected","clientId":"client-1"}"""u8,
            CortanaWsJsonContext.Default.CortanaWsServerMessage);

        Assert.IsNotNull(message);
        Assert.AreEqual("connected", message.Type);
        Assert.AreEqual("client-1", message.ClientId);
    }

    [TestMethod]
    public void Serialize_ClientMessage_OmitsNullFields()
    {
        var json = JsonSerializer.Serialize(
            new CortanaWsClientMessage { Type = "send", Data = "hello" },
            CortanaWsJsonContext.Default.CortanaWsClientMessage);

        Assert.AreEqual("""{"type":"send","data":"hello"}""", json);
    }
}
