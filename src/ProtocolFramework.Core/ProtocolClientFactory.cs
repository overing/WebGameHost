
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace ProtocolFramework.Core;

public interface IProtocolClientFactory
{
    Task<IProtocolClient> ConnectAsync(
        string address,
        int port,
        Action<IProtocolRouteBuilder>? configRoute = null,
        CancellationToken cancellationToken = default);
}

internal sealed class ProtocolClientFactory(IServiceProvider serviceProvider, IProtocolRouteBuilder protocolRouteBuilder)
    : IProtocolClientFactory
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IProtocolRouteBuilder _protocolRouteBuilder = protocolRouteBuilder;

    public async Task<IProtocolClient> ConnectAsync(
        string address,
        int port,
        Action<IProtocolRouteBuilder>? configRoute,
        CancellationToken cancellationToken)
    {
        var socket = default(Socket?);

        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(address, port);

            var connection = new SocketProtocolConnection(socket);

            var builder = _protocolRouteBuilder;
            if (configRoute is { })
            {
                builder = builder.Clone();
                configRoute.Invoke(builder);
            }

            var client = ActivatorUtilities.CreateInstance<ProtocolClient>(_serviceProvider, connection, builder.Build());
            client.StartProcessReceive(cancellationToken);

            return client;
        }
        catch
        {
            socket?.Dispose();

            throw;
        }
    }
}
