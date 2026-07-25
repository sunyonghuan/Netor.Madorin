using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Entities;

[JsonConverter(typeof(JsonStringEnumConverter<SessionStatus>))]
public enum SessionStatus
{
    Active,
    Recovered,
    Archived
}
