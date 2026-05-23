# C# PluginBus Template

## Files

```text
Samples/PluginBusClient/
  MadorinPluginBusClientSample.csproj
  Program.cs
  MadorinPluginBusClient.cs
  PluginBusContracts.cs
  PluginBusJsonContext.cs
```

## Program.cs

```csharp
var endpoint = args.Length > 0 ? args[0] : "ws://localhost:52841/internal";

await using var client = new MadorinPluginBusClient(endpoint);
await client.ConnectAsync();
await client.SubscribeAsync(["conversation"]);

await foreach (var token in client.SendAndStreamAsync("text"))
{
  Console.Write(token);
}
```

## Client Rules

- connect -> wait `connected`
- send `subscribe`
- send `chat.message.send`
- read `payload.type=token/done/error`
- reply `ping` with `pong`
- use source generator

## Contracts

```csharp
public sealed record PluginBusMessage
{
  [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
  [JsonPropertyName("protocol")] public string? Protocol { get; init; }
  [JsonPropertyName("version")] public string? Version { get; init; }
  [JsonPropertyName("topic")] public string? Topic { get; init; }
  [JsonPropertyName("op")] public string? Op { get; init; }
  [JsonPropertyName("payload")] public JsonElement Payload { get; init; }
}
```
