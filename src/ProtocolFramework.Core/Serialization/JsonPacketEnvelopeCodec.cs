
using System.Text;
using System.Text.Json;

namespace ProtocolFramework.Core.Serialization;

public sealed class JsonPacketEnvelopeCodec(
    IPacketTypeResolver typeResolver,
    JsonSerializerOptions? options)
    : IPacketEnvelopeCodec
{
    private readonly IPacketTypeResolver _typeResolver = typeResolver ?? throw new ArgumentNullException(nameof(typeResolver));
    private readonly JsonSerializerOptions _options = options ?? new JsonSerializerOptions();

    public JsonPacketEnvelopeCodec(IPacketTypeResolver typeResolver) : this(typeResolver, null) { }

    public byte[] Encode<T>(T packet) where T : class
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (!_typeResolver.TryGetTypeName(typeof(T), out var typeName))
            throw new InvalidOperationException($"Type '{typeof(T)}' is not registered. Register it in PacketTypeResolverOptions.");

        var wrapper = new EnvelopeDto
        {
            TypeName = typeName,
            Payload = JsonSerializer.SerializeToElement(packet, _options)
        };

        return JsonSerializer.SerializeToUtf8Bytes(wrapper, _options);
    }

    private const string TypeName = nameof(EnvelopeDto.TypeName);
    private const string Payload = nameof(EnvelopeDto.Payload);

    public PacketEnvelope Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        if (!root.TryGetProperty(TypeName, out var typeNameElement))
            throw new FormatException($"Missing {TypeName} property");

        var typeName = typeNameElement.GetString()
            ?? throw new FormatException($"{TypeName} is null");

        if (!root.TryGetProperty(Payload, out var payloadElement))
            throw new FormatException($"Missing {Payload} property");

        var payloadJson = payloadElement.GetRawText();
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        return new(typeName, payloadBytes);
    }

    internal sealed class EnvelopeDto
    {
        public string TypeName { get; set; } = default!;
        public JsonElement Payload { get; set; }
    }
}
