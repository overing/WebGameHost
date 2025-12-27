
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

    public PacketEnvelope Decode(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
            throw new ArgumentException("Data cannot be empty", nameof(data));

        var reader = new Utf8JsonReader(data.Span);

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new FormatException("Expected JSON object");

        string? typeName = null;
        ReadOnlyMemory<byte> payload = default;
        bool foundTypeName = false;
        bool foundPayload = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("TypeName"u8))
                {
                    reader.Read();
                    typeName = reader.GetString();
                    foundTypeName = true;
                }
                else if (reader.ValueTextEquals("Payload"u8))
                {
                    reader.Read();
                    long start = reader.TokenStartIndex;
                    reader.Skip();
                    long end = reader.BytesConsumed;

                    int length = (int)(end - start);
                    payload = data.Slice((int)start, length);
                    foundPayload = true;
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }

        if (!foundTypeName || typeName is null)
            throw new FormatException($"Missing {TypeName} property");

        if (!foundPayload)
            throw new FormatException($"Missing {Payload} property");

        return new(typeName, payload);
    }

    internal sealed class EnvelopeDto
    {
        public string TypeName { get; set; } = default!;
        public JsonElement Payload { get; set; }
    }
}
