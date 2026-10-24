using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PulseSharp.Stores;

namespace PulseSharp.Sync;

internal static class SyncProtocol
{
    public const int Version = 1;
    public const string ProtocolHeader = "X-PulseSharp-Protocol";
    public const string NodeHeader = "X-PulseSharp-Node";
    public const string KeyHeader = "X-PulseSharp-Key";
}

internal sealed record WireEvent(
    Guid Id,
    string Stream,
    string Name,
    JsonElement Data,
    string Origin,
    DateTimeOffset EmittedAt,
    long Position
)
{
    public static WireEvent From(StoredEvent stored)
    {
        using var document = JsonDocument.Parse(stored.Data);
        return new WireEvent(
            stored.EventId,
            stored.Stream,
            stored.EventName,
            document.RootElement.Clone(),
            stored.Origin,
            stored.EmittedAt,
            stored.GlobalPosition
        );
    }

    public NewEvent ToNewEvent()
    {
        return new NewEvent(Id, Stream, Name, Encoding.UTF8.GetBytes(Data.GetRawText()), Origin, EmittedAt);
    }
}

internal sealed record PushRequest(IReadOnlyList<WireEvent> Events);

internal sealed record PushResponse(int Accepted, int Duplicates, int Refused = 0);

internal sealed record PullResponse(IReadOnlyList<WireEvent> Events, long Next, bool HasMore);

internal sealed record ServerInfo(int Protocol, string Node);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(PushRequest))]
[JsonSerializable(typeof(PushResponse))]
[JsonSerializable(typeof(PullResponse))]
[JsonSerializable(typeof(ServerInfo))]
internal sealed partial class WireJson : JsonSerializerContext;
