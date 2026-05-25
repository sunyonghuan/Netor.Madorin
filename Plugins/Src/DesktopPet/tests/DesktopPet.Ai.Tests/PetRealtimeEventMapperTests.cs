using DesktopPet.Behaviors;

namespace DesktopPet.Ai.Tests;

[TestClass]
public sealed class PetRealtimeEventMapperTests
{
    [TestMethod]
    public void Map_Token_ReturnsTextDelta()
    {
        var mapper = new PetRealtimeEventMapper();

        var petEvent = mapper.Map(new CortanaWsServerMessage { Type = "token", Data = "你好" });

        Assert.IsNotNull(petEvent);
        Assert.AreEqual(PetEventKind.TextDelta, petEvent.Kind);
        Assert.AreEqual("你好", petEvent.Text);
    }

    [TestMethod]
    public void Map_TtsSubtitle_ReturnsSpeak()
    {
        var mapper = new PetRealtimeEventMapper();

        var petEvent = mapper.Map(new CortanaWsServerMessage { Type = "tts_subtitle", Data = "正在播放" });

        Assert.IsNotNull(petEvent);
        Assert.AreEqual(PetEventKind.Speak, petEvent.Kind);
        Assert.AreEqual("正在播放", petEvent.Text);
    }

    [TestMethod]
    public void Map_ChatCompleted_ReturnsClearText()
    {
        var mapper = new PetRealtimeEventMapper();

        var petEvent = mapper.Map(new CortanaWsServerMessage { Type = "chat_completed" });

        Assert.IsNotNull(petEvent);
        Assert.AreEqual(PetEventKind.ClearText, petEvent.Kind);
    }

    [TestMethod]
    public void Map_UnknownType_ReturnsNull()
    {
        var mapper = new PetRealtimeEventMapper();

        var petEvent = mapper.Map(new CortanaWsServerMessage { Type = "future_event" });

        Assert.IsNull(petEvent);
    }
}
