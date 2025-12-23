
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Core;

public interface IProtocolClient : IDisposable
{
    CancellationToken ConnectionClosed { get; }
    void StartProcessReceive(CancellationToken cancellationToken = default);
    Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class;
    Task DisconnectAsync();
}

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
        catch (Exception ex)
        {
            _logger.LogReceiveError(LogLevel.Error, ex, _session.SessionId);
        }
    }

    public Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class
        => _session.SendAsync(packet, cancellationToken);

    public async Task DisconnectAsync()
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
}

internal static partial class ProtocolClientLoggerExtensions
{
    [LoggerMessage("Session#{SessionId} Close")]
    public static partial void LogSessionClosed(this ILogger logger, LogLevel logLevel, string sessionId);
    [LoggerMessage("Session#{SessionId} Receive Error")]
    public static partial void LogReceiveError(this ILogger logger, LogLevel logLevel, Exception exception, string sessionId);
}
