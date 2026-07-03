using System.Reflection;
using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.UI.Tests;

[TestClass]
public sealed class UiChatOutputChannelTests
{
    [TestMethod]
    public async Task OnCancelledAsync_WhenOldTurnCancelled_NewTurnIsNotIgnored()
    {
        var channel = CreateChannel();

        await channel.OnCancelledAsync("turn-old");

        Assert.IsTrue(InvokeShouldIgnoreTurn(channel, "turn-old", CancellationToken.None));
        Assert.IsFalse(InvokeShouldIgnoreTurn(channel, "turn-new", CancellationToken.None));
    }

    [TestMethod]
    public async Task OnTokenAsync_WhenOldTurnWasCancelled_DoesNotCreateStateButNewTurnStillCreatesState()
    {
        var channel = CreateChannel();

        await channel.OnCancelledAsync("turn-old");
        await channel.OnTokenAsync("turn-old", "stale-token", "session-1");
        await channel.OnTokenAsync("turn-new", "fresh-token", "session-1");

        var states = GetStatesByTurnId(channel);
        Assert.IsFalse(states.ContainsKey("turn-old"));
        Assert.IsTrue(states.ContainsKey("turn-new"));
    }

    [TestMethod]
    public async Task OnTokenAsync_WhenTokenIsEmpty_DoesNotCreateTurnState()
    {
        var channel = CreateChannel();

        await channel.OnTokenAsync("turn-empty", string.Empty, "session-1");

        var states = GetStatesByTurnId(channel);
        Assert.IsFalse(states.ContainsKey("turn-empty"));
    }

    [TestMethod]
    public void HandleProcessEvent_WhenToolEventHasNoDisplayContent_DoesNotCreateToolGroup()
    {
        var channel = CreateChannel();

        InvokeHandleProcessEvent(channel, new RealtimeProcessEvent
        {
            TurnId = "turn-empty-tool",
            ProcessId = "tool-empty",
            Kind = "tool",
            Status = "success",
            Content = string.Empty,
            ExitCode = 0,
            DurationMs = 1,
            Timestamp = DateTimeOffset.UtcNow
        });

        var state = GetStatesByTurnId(channel)["turn-empty-tool"];
        Assert.AreEqual(0, GetPrivateDictionaryCount(state, "ToolGroupsByTurnId"));
        Assert.AreEqual(0, GetPrivateDictionaryCount(state, "ToolGroupsByProcessId"));
    }

    [TestMethod]
    public void MarkTurnCancelled_WhenSpecificTurnCancelled_OnlyThatTurnIsRecorded()
    {
        var channel = CreateChannel();

        InvokeMarkTurnCancelled(channel, "turn-a");
        InvokeMarkTurnCancelled(channel, "turn-b");

        var cancelledTurns = GetCancelledTurnIds(channel);
        Assert.IsTrue(cancelledTurns.ContainsKey("turn-a"));
        Assert.IsTrue(cancelledTurns.ContainsKey("turn-b"));
        Assert.IsFalse(cancelledTurns.ContainsKey("turn-c"));
    }

    [TestMethod]
    public void ResetTurn_WhenCancelledTurnStateRemoved_DoesNotAffectOtherTurnState()
    {
        var channel = CreateChannel();

        CreateTurnState(channel, "turn-a");
        CreateTurnState(channel, "turn-b");

        InvokeResetTurn(channel, "turn-a", cancelToolGroups: true, toolCancelReason: "cancelled");

        var states = GetStatesByTurnId(channel);
        Assert.IsFalse(states.ContainsKey("turn-a"));
        Assert.IsTrue(states.ContainsKey("turn-b"));
    }

    private static UiChatOutputChannel CreateChannel()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new UiChatOutputChannel(services, NullLogger<UiChatOutputChannel>.Instance);
    }

    private static bool InvokeShouldIgnoreTurn(UiChatOutputChannel channel, string turnId, CancellationToken cancellationToken)
    {
        return (bool)InvokePrivateMethod(channel, "ShouldIgnoreTurn", turnId, cancellationToken)!;
    }

    private static void InvokeMarkTurnCancelled(UiChatOutputChannel channel, string turnId)
    {
        _ = InvokePrivateMethod(channel, "MarkTurnCancelled", turnId);
    }

    private static void InvokeResetTurn(UiChatOutputChannel channel, string turnId, bool cancelToolGroups, string toolCancelReason)
    {
        _ = InvokePrivateMethod(channel, "ResetTurn", turnId, cancelToolGroups, toolCancelReason);
    }

    private static void InvokeHandleProcessEvent(UiChatOutputChannel channel, RealtimeProcessEvent evt)
    {
        _ = InvokePrivateMethod(channel, "HandleProcessEvent", evt);
    }

    private static ConcurrentDictionary<string, DateTimeOffset> GetCancelledTurnIds(UiChatOutputChannel channel)
    {
        return GetPrivateField<ConcurrentDictionary<string, DateTimeOffset>>(channel, "_cancelledTurnIds");
    }

    private static ConcurrentDictionary<string, object> GetStatesByTurnId(UiChatOutputChannel channel)
    {
        var field = channel.GetType().GetField("_statesByTurnId", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);

        var raw = field.GetValue(channel);
        Assert.IsNotNull(raw);

        var result = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);
        var enumerable = (System.Collections.IEnumerable)raw;
        foreach (var item in enumerable)
        {
            var itemType = item.GetType();
            var key = (string)itemType.GetProperty("Key")!.GetValue(item)!;
            var value = itemType.GetProperty("Value")!.GetValue(item)!;
            result[key] = value;
        }

        return result;
    }

    private static void CreateTurnState(UiChatOutputChannel channel, string turnId)
    {
        var field = channel.GetType().GetField("_statesByTurnId", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var dictionary = field.GetValue(channel);
        Assert.IsNotNull(dictionary);

        var genericArgs = dictionary.GetType().GenericTypeArguments;
        var turnStateType = genericArgs[1];
        var turnState = Activator.CreateInstance(turnStateType, nonPublic: true);
        Assert.IsNotNull(turnState);

        var tryAdd = dictionary.GetType().GetMethod("TryAdd");
        Assert.IsNotNull(tryAdd);
        _ = tryAdd.Invoke(dictionary, [turnId, turnState]);
    }

    private static object? InvokePrivateMethod(object instance, string methodName, params object?[] args)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, $"未找到私有方法：{methodName}");
        return method.Invoke(instance, args);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"未找到私有字段：{fieldName}");
        return (T)field.GetValue(instance)!;
    }

    private static int GetPrivateDictionaryCount(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        Assert.IsNotNull(property, $"未找到属性：{propertyName}");

        var value = property.GetValue(instance);
        Assert.IsNotNull(value);

        var countProperty = value.GetType().GetProperty("Count");
        Assert.IsNotNull(countProperty, $"未找到 Count 属性：{propertyName}");
        return (int)countProperty.GetValue(value)!;
    }
}
