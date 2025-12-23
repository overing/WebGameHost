
namespace ProtocolFramework.Core.Serialization;

/// <summary>
/// 封包封裝/解封的編解碼器
/// </summary>
public interface IPacketEnvelopeCodec
{
    /// <summary>
    /// 將封包封裝為可傳輸的 byte[]
    /// </summary>
    byte[] Encode<T>(T packet) where T : class;

    /// <summary>
    /// 解封並取得類型名稱與原始資料
    /// </summary>
    PacketEnvelope Decode(byte[] data);
}

/// <summary>
/// 解封後的封包資訊
/// </summary>
public sealed class PacketEnvelope(string typeName, ReadOnlyMemory<byte> payload)
{
    public string TypeName { get; } = typeName ?? throw new ArgumentNullException(nameof(typeName));
    public ReadOnlyMemory<byte> Payload { get; } = payload;
}
