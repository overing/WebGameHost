
using System.Net.Sockets;

namespace ProtocolFramework.Core;

public sealed class ProtocolClient(IProtocolConnection connection, ProtocolRoute route) : IDisposable
{
    private readonly IProtocolConnection _connection = connection;
    private readonly ProtocolSession _session = new(connection);
    private readonly ProtocolConnectionProcessor _processor = new ProtocolConnectionProcessor(route);
    private Task? _receiveTask;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 開始接收 - 使用相同的處理器
    /// </summary>
    public void StartReceiving()
    {
        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(async () =>
        {
            try
            {
                // 客戶端和伺服器使用完全相同的邏輯！
                await _processor.ProcessPacketsAsync(
                    _connection,  // IProtocolReader
                    _session,
                    serviceProvider: null,
                    _cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Receive error: {ex.Message}");
            }
        });
    }

    public Task SendAsync<TPacket>(TPacket packet, CancellationToken ct = default)
    {
        return _session.SendAsync(packet, ct);
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();
        if (_receiveTask != null)
            await _receiveTask;
        await _connection.CloseAsync();
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _session.Dispose();
    }

    public static async Task<ProtocolClient> ConnectAsync(
        string address,
        int port,
        Action<ProtocolRouteBuilder> routeConfig,
        CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync("127.0.0.1", 5100);

        var connection = new SocketProtocolConnection(socket);

        var builder = new ProtocolRouteBuilder();
        routeConfig(builder);

        var client = new ProtocolClient(connection, builder.Build());
        client.StartReceiving();

        return client;
    }
}
