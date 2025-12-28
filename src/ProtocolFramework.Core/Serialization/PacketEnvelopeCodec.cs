
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.ObjectPool;

namespace ProtocolFramework.Core.Serialization;

/// <summary>
/// 封包封裝/解封的編解碼器
/// </summary>
public interface IPacketEnvelopeCodec
{
    /// <summary>
    /// 將封包封裝為可傳輸的 IMemoryOwner<byte>
    /// </summary>
    IMemoryOwner<byte> Encode<T>(T packet) where T : class;

    /// <summary>
    /// 解封並取得類型名稱與原始資料
    /// </summary>
    PacketEnvelope Decode(ReadOnlyMemory<byte> data);
}

/// <summary>
/// 解封後的封包資訊
/// </summary>
public sealed class PacketEnvelope(string typeName, ReadOnlyMemory<byte> payload)
{
    private readonly ReadOnlyMemory<byte> _payload = payload;
    public string TypeName { get; } = typeName ?? throw new ArgumentNullException(nameof(typeName));
    public ReadOnlySpan<byte> PayloadSpan => _payload.Span;
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class JsonPacketEnvelopeCodec(IPacketTypeResolver typeResolver, JsonSerializerOptions? options)
    : IPacketEnvelopeCodec
{
    private readonly IPacketTypeResolver _typeResolver = typeResolver ?? throw new ArgumentNullException(nameof(typeResolver));
    private readonly JsonSerializerOptions _options = options ?? new JsonSerializerOptions();
    private readonly ObjectPool<ArrayBufferWriter<byte>> _writerPool = ObjectPool.Create(new ArrayBufferWriterPooledObjectPolicy());

    public JsonPacketEnvelopeCodec(IPacketTypeResolver typeResolver) : this(typeResolver, null) { }

    public IMemoryOwner<byte> Encode<T>(T packet) where T : class
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (!_typeResolver.TryGetTypeName(typeof(T), out var typeName))
            throw new InvalidOperationException($"Type '{typeof(T)}' is not registered.");

        var writer = _writerPool.Get();
        try
        {
            using var jsonWriter = new Utf8JsonWriter(writer);
            jsonWriter.WriteStartObject();
            jsonWriter.WriteString("TypeName"u8, typeName);
            jsonWriter.WritePropertyName("Payload"u8);
            JsonSerializer.Serialize(jsonWriter, packet, _options);
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();

            var array = ArrayPool<byte>.Shared.Rent(writer.WrittenCount);
            writer.WrittenSpan.CopyTo(array);
            return new PooledMemoryOwner(array, writer.WrittenCount);
        }
        finally
        {
            writer.Clear();
            _writerPool.Return(writer);
        }
    }

    private const string TypeName = "TypeName";
    private const string Payload = "Payload";

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

    private sealed class ArrayBufferWriterPooledObjectPolicy : PooledObjectPolicy<ArrayBufferWriter<byte>>
    {
        // 預設初始容量，足夠大多數封包使用
        private const int InitialCapacity = 256;
        
        public override ArrayBufferWriter<byte> Create() => new(InitialCapacity);

        public override bool Return(ArrayBufferWriter<byte> obj)
        {
            // 如果 buffer 成長太大，丟棄它以避免記憶體浪費
            const int MaxRetainedCapacity = 64 * 1024;  // 64KB
            return obj.Capacity <= MaxRetainedCapacity;
        }
    }
}
