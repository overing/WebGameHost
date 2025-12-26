
using System.Text.Json;

namespace ProtocolFramework.Core.Serialization;

public sealed class JsonPayloadSerializer(JsonSerializerOptions? options) : IPayloadSerializer
{
    private readonly JsonSerializerOptions _options = options ?? new JsonSerializerOptions();

    public JsonPayloadSerializer() : this(null) { }

    public byte[] Serialize(object packet, Type type)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(type);

        return JsonSerializer.SerializeToUtf8Bytes(packet, type, _options);
    }

    public object Deserialize(ReadOnlySpan<byte> data, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        return JsonSerializer.Deserialize(data, targetType, _options)
            ?? throw new FormatException($"Deserialization returned null for type {targetType}");
    }
}
