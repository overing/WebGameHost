
namespace ProtocolFramework.Core;

/// <summary>
/// 完整的協定連線（組合讀寫）
/// </summary>
public interface IProtocolConnection : IProtocolReader, IProtocolWriter
{
    ValueTask CloseAsync();
}
