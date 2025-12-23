
namespace ProtocolFramework.Core.Serialization;

/// <summary>
/// 封包內容序列化器
/// </summary>
public interface IPayloadSerializer
{
    /// <summary>
    /// 序列化物件
    /// </summary>
    byte[] Serialize(object packet, Type type);

    /// <summary>
    /// 反序列化物件
    /// </summary>
    object Deserialize(ReadOnlySpan<byte> data, Type targetType);
}
