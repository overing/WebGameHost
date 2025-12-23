
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProtocolFramework.Core;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Host;

[SuppressMessage("Performance", "CA1812", Justification = "這個類別透過 DI 建立")]
internal sealed class AspNetCoreProtocolConnectionHandler(
    ILogger<AspNetCoreProtocolConnectionHandler> logger,
    IPacketEnvelopeCodec packetEnvelopeCodec,
    IProtocolRouteBuilder routeBuilder,
    IServiceScopeFactory serviceScopeFactory) : ConnectionHandler
{
    private readonly ProtocolConnectionProcessor _processor = new(routeBuilder.Build());
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly ILogger _logger = logger;

    [SuppressMessage("Design", "CA1031", Justification = "無法預知框架使用者可能拋出的錯誤")]
    public override async Task OnConnectedAsync(ConnectionContext connection)
    {
        var protocolConnection = new AspNetCoreProtocolConnection(connection);
        using var session = new ProtocolSession(protocolConnection, packetEnvelopeCodec);

        _logger.LogClientConnected(LogLevel.Information, session.SessionId);

        try
        {
            var ct = connection.Features.Get<IConnectionLifetimeFeature>()?.ConnectionClosed
                ?? CancellationToken.None;

            await _processor
                .ProcessPacketsAsync(protocolConnection, session, _serviceScopeFactory, ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);
        }
        catch (ConnectionResetException)
        {
            _logger.LogClientReset(LogLevel.Information, session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogPacketProcessError(LogLevel.Error, ex, session.SessionId);
        }
        finally
        {
            _logger.LogClientDisconnected(LogLevel.Information, session.SessionId);
        }
    }
}

internal static partial class ProtocolConnectionHandlerLoggerExtensions
{
    [LoggerMessage("Client connected: {SessionId}")]
    public static partial void LogClientConnected(this ILogger logger, LogLevel logLevel, string sessionId);

    [LoggerMessage("Error processing packets for session {SessionId}")]
    public static partial void LogPacketProcessError(this ILogger logger, LogLevel logLevel, Exception exception, string sessionId);

    [LoggerMessage("Client reset: {SessionId}")]
    public static partial void LogClientReset(this ILogger logger, LogLevel logLevel, string sessionId);

    [LoggerMessage("Client disconnected: {SessionId}")]
    public static partial void LogClientDisconnected(this ILogger logger, LogLevel logLevel, string sessionId);

    [LoggerMessage("Error handling packets for session {SessionId}")]
    public static partial void LogPacketHandleError(this ILogger logger, LogLevel logLevel, Exception exception, string sessionId);
}
