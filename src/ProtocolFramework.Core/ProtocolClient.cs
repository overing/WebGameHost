
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace ProtocolFramework.Core;

public sealed class ProtocolClient(
    ILogger<ProtocolClient> logger,
    IProtocolConnection connection,
    ProtocolRoute route)
    : IDisposable
{
    private readonly ILogger _logger = logger;
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
                    _connection,
                    _session,
                    serviceScopeFactory: null,
                    _cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogError($"Receive error: {ex.Message}");
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
        ILoggerFactory loggerFactory,
        string address,
        int port,
        Action<ProtocolRouteBuilder> routeConfig)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(address, port);

        var connection = new SocketProtocolConnection(socket);

        var builder = new ProtocolRouteBuilder();
        routeConfig(builder);

        var logger = loggerFactory.CreateLogger<ProtocolClient>();
        var client = new ProtocolClient(logger, connection, builder.Build());
        client.StartReceiving();

        return client;
    }
}
