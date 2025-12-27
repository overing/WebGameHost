
namespace ProtocolFramework.Core;

public interface IProtocolErrorHandler
{
    ValueTask OnUnknownPacketAsync(string typeName, IProtocolSession session);
    ValueTask OnHandlerExceptionAsync(Exception exception, object packet, IProtocolSession session);
}
