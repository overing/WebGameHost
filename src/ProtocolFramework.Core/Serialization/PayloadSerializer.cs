
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ProtocolFramework.Core.Serialization;

/// <summary>
/// 封包內容序列化器
/// </summary>
public interface IPayloadSerializer
{
    /// <summary>
    /// 反序列化物件
    /// </summary>
    object Deserialize(ReadOnlySpan<byte> data, Type targetType);
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class JsonPayloadSerializer(JsonSerializerOptions? options) : IPayloadSerializer
{
    private readonly JsonSerializerOptions _options = options ?? new JsonSerializerOptions();

    public JsonPayloadSerializer() : this(null) { }

    public object Deserialize(ReadOnlySpan<byte> data, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);

        return JsonSerializer.Deserialize(data, targetType, _options)
            ?? throw new FormatException($"Deserialization returned null for type {targetType}");
    }
}
