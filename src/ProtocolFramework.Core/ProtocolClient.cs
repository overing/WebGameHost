
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Core;

public interface IProtocolClient : IDisposable, IAsyncDisposable
{
    IProtocolSession ProtocolSession { get; }
    CancellationToken ConnectionClosed { get; }
    void StartProcessReceive(CancellationToken cancellationToken = default);
    ValueTask SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class;
    ValueTask DisconnectAsync();
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class ProtocolClient(
    ILogger<ProtocolClient> logger,
    IServiceScopeFactory serviceScopeFactory,
    IProtocolConnection connection,
    IPacketEnvelopeCodec packetEnvelopeCodec,
    IProtocolRouteBuilder protocolRouteBuilder)
    : IProtocolClient
{
    private readonly ILogger _logger = logger;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly IProtocolConnection _connection = connection;
    private readonly ProtocolSession _session = new(connection, packetEnvelopeCodec);
    private readonly ProtocolConnectionProcessor _processor = new(protocolRouteBuilder);
    private Task? _receiveTask;

    public IProtocolSession ProtocolSession => _session;
    public CancellationToken ConnectionClosed => _session.SessionClosed;

    public void StartProcessReceive(CancellationToken cancellationToken = default)
    {
        if (_receiveTask != null)
            throw new InvalidOperationException("已啟動");

        _receiveTask = ProcessReceiveAsync(cancellationToken);
    }

    private async Task ProcessReceiveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 客戶端和伺服器使用完全相同的邏輯！
            await _processor
                .ProcessPacketsAsync(
                    _connection,
                    _session,
                    _serviceScopeFactory,
                    cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (_session.SessionClosed.IsCancellationRequested)
        {
            _logger.LogSessionClosed(LogLevel.Debug, _session.SessionId);
        }
#pragma warning disable CA1031 // 接收迴圈需要捕獲所有例外以避免未觀察的 Task 例外
        catch (Exception ex)
        {
            _logger.LogReceiveError(LogLevel.Error, ex, _session.SessionId);
        }
#pragma warning restore CA1031
    }

    public ValueTask SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class
        => _session.SendAsync(packet, cancellationToken);

    public async ValueTask DisconnectAsync()
    {
        await _session.CloseAsync().ConfigureAwait(false);

        if (_receiveTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _connection.CloseAsync().ConfigureAwait(false);
    }

    public void Dispose() => _session.Dispose();

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _session.Dispose();
    }
}

internal static partial class ProtocolClientLoggerExtensions
{
    [LoggerMessage("Session#{SessionId} Close")]
    public static partial void LogSessionClosed(this ILogger logger, LogLevel logLevel, string sessionId);
    [LoggerMessage("Session#{SessionId} Receive Error")]
    public static partial void LogReceiveError(this ILogger logger, LogLevel logLevel, Exception exception, string sessionId);
}

public interface IProtocolClientFactory
{
    Task<IProtocolClient> ConnectAsync(
        string address,
        int port,
        string token,
        Action<IProtocolRouteBuilder>? configRoute = null,
        CancellationToken cancellationToken = default);
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class WebSocketProtocolClientFactory(
    IServiceProvider serviceProvider,
    IProtocolRouteBuilder protocolRouteBuilder)
    : IProtocolClientFactory
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IProtocolRouteBuilder _protocolRouteBuilder = protocolRouteBuilder;

    public async Task<IProtocolClient> ConnectAsync(
        string address,
        int port,
        string token,
        Action<IProtocolRouteBuilder>? configRoute,
        CancellationToken cancellationToken)
    {
        var socket = default(ClientWebSocket?);
        var stream = default(Stream?);
        var connection = default(StreamProtocolConnection?);

        try
        {
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
            var wsUri = new Uri($"ws://{address}:{port}/ws?access_token={token}");
            await socket.ConnectAsync(wsUri, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

#pragma warning disable CA2000 // stream 所有權轉移給 connection，由 finally 統一處理
            stream = WebSocketStream.Create(socket, WebSocketMessageType.Binary, ownsWebSocket: true);
#pragma warning restore CA2000
            connection = new StreamProtocolConnection(stream);

            var builder = _protocolRouteBuilder;
            if (configRoute is { })
            {
                builder = builder.Clone();
                configRoute.Invoke(builder);
            }

            var client = ActivatorUtilities.CreateInstance<ProtocolClient>(_serviceProvider, connection);
            client.StartProcessReceive(cancellationToken);

            // 成功後將所有權轉移給 client，清除本地引用
            socket = null;
            stream = null;
            connection = null;

            return client;
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
            else if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            
            socket?.Dispose();
        }
    }
}
