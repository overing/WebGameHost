
namespace ProtocolFramework.Core;

public interface IProtocolSession
{
    string SessionId { get; }
    Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default);
    ValueTask CloseAsync();
    IDictionary<string, object> Properties { get; }
}
