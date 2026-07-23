using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Entities;

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeMode>))]
public enum RuntimeMode
{
    Expert,
    Meeting,
    Work
}
