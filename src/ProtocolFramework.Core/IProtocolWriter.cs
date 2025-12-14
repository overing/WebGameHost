
namespace ProtocolFramework.Core;

/// <summary>
/// 協定資料寫入器
/// </summary>
public interface IProtocolWriter
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default);
}
