
namespace ProtocolFramework.Core;

/// <summary>
/// 完整的協定連線（組合讀寫）
/// </summary>
public interface IProtocolConnection : IProtocolReader, IProtocolWriter
{
    ValueTask CloseAsync();
}

/// <summary>
/// 協定資料讀取器
/// </summary>
public interface IProtocolReader
{
    /// <summary>
    /// 讀取完整封包
    /// </summary>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 協定資料寫入器
/// </summary>
public interface IProtocolWriter
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default);
}
