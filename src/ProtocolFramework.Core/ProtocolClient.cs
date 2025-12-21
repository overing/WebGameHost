
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProtocolFramework.Core;

public interface IProtocolClient : IDisposable
{
    /// <summary>
    /// 開始接收 - 使用相同的處理器
    /// </summary>
    void StartProcessReceive(CancellationToken cancellationToken = default);
    Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}

internal sealed class ProtocolClient(
    ILogger<ProtocolClient> logger,
    IServiceScopeFactory serviceScopeFactory,
    IProtocolConnection connection,
    ProtocolRoute route)
    : IProtocolClient
{
    private readonly ILogger _logger = logger;
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly IProtocolConnection _connection = connection;
    private readonly ProtocolSession _session = new(connection);
    private readonly ProtocolConnectionProcessor _processor = new(route);
    private Task? _receiveTask;
    private CancellationTokenSource? _cts;

    public void StartProcessReceive(CancellationToken cancellationToken = default)
    {
        if (_receiveTask != null)
            throw new InvalidOperationException("已啟動");

        _receiveTask = ProcessReceiveAsync(cancellationToken);
    }

    private async Task ProcessReceiveAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // 客戶端和伺服器使用完全相同的邏輯！
            await _processor.ProcessPacketsAsync(
                _connection,
                _session,
                _serviceScopeFactory,
                _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogReceiveError(LogLevel.Error, ex);
        }
    }

    public Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default)
        => _session.SendAsync(packet, cancellationToken);

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
}

internal static partial class ProtocolClientLoggerExtensions
{
    [LoggerMessage("Receive Error")]
    public static partial void LogReceiveError(this ILogger logger, LogLevel logLevel, Exception exception);
}
