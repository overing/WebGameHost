
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProtocolFramework.Core;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Host;

public interface IWebSocketConnectionManager
{
    Task HandleConnectionAsync(HttpContext httpContext, string userId, WebSocket webSocket);
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class WebSocketConnectionManager(
    ILogger<WebSocketConnectionManager> logger,
    ILoggerFactory loggerFactory,
    IServiceScopeFactory serviceScopeFactory,
    IProtocolRouteBuilder routeBuilder,
    IPacketEnvelopeCodec codec,
    IProtocolErrorHandler? errorHandler = null)
    : IWebSocketConnectionManager
{
    private readonly ILogger _logger = logger;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ConcurrentDictionary<string, IProtocolSession> _sessions = new();
    private readonly Lazy<IProtocolRoute> _route = new(routeBuilder.Build);
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly IPacketEnvelopeCodec _codec = codec;
    private readonly IProtocolErrorHandler? _errorHandler = errorHandler;

    public async Task HandleConnectionAsync(HttpContext httpContext, string userId, WebSocket webSocket)
    {
        using var stream = WebSocketStream.Create(webSocket, WebSocketMessageType.Binary, ownsWebSocket: true);
        using var connection = new StreamProtocolConnection(stream);
        var logger = _loggerFactory.CreateLogger<ProtocolSession>();

        using var session = new ProtocolSession(
            logger,
            connection,
            _codec,
            _route.Value,
            _serviceScopeFactory,
            _errorHandler);

        _sessions[userId] = session;

#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            await session.RunAsync(httpContext.RequestAborted).ConfigureAwait(ConfigureAwaitOptions.None);
        }
        catch (OperationCanceledException)
        {
            // 正常取消（httpContext.RequestAborted 或 session 關閉）
        }
        catch (Exception ex) when (IsExpectedDisconnection(ex))
        {
            // 預期中的斷線，記錄為 Debug
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogUserDisconnectedWithReason(LogLevel.Debug, userId, ex.GetType().Name);
        }
        catch (Exception ex)
        {
            // 非預期的錯誤，記錄為 Warning
            _logger.LogUnexpectedDisconnection(LogLevel.Warning, ex, userId);
        }
        finally
        {
            _sessions.TryRemove(userId, out _);
            _logger.LogUserDisconnected(LogLevel.Information, userId);
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    private static bool IsExpectedDisconnection(Exception ex)
    {
        return ex is IOException
            or SocketException
            or WebSocketException
            || ex.InnerException is not null && IsExpectedDisconnection(ex.InnerException);
    }

    public async Task BroadcastAsync<TPacket>(TPacket packet) where TPacket : class
    {
        foreach (var (_, session) in _sessions)
        {
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                await session.SendAsync(packet).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch
            {
                // Ignore errors
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }
    }
}

internal static partial class ProtocolConnectionHandlerLoggerExtensions
{
    [LoggerMessage("Client connected: {SessionId}")]
    public static partial void LogClientConnected(this ILogger logger, LogLevel logLevel, string sessionId);

    [LoggerMessage("Error processing packets for session {SessionId}")]
    public static partial void LogPacketProcessError(this ILogger logger, LogLevel logLevel, Exception exception, string sessionId);

    [LoggerMessage("Client disconnected: {SessionId}")]
    public static partial void LogClientDisconnected(this ILogger logger, LogLevel logLevel, string sessionId);

    [LoggerMessage("User '{UserId}' disconnected ({Reason})")]
    public static partial void LogUserDisconnectedWithReason(this ILogger logger, LogLevel logLevel, string userId, string reason);

    [LoggerMessage("User '{UserId}' disconnected")]
    public static partial void LogUserDisconnected(this ILogger logger, LogLevel logLevel, string userId);

    [LoggerMessage("Unexpected disconnection for user '{UserId}'")]
    public static partial void LogUnexpectedDisconnection(this ILogger logger, LogLevel logLevel, Exception exception, string userId);
}

public static class AspNetCoreProtocolHostExtensions
{
    public static IProtocolRouteBuilder MapProtocol(this IHost host, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(host);

        var protocolRouteBuilder = host.Services.GetService<IProtocolRouteBuilder>()
            ?? throw new InvalidOperationException($"Require {nameof(AddAspNetCoreProtocolHost)}");
        return protocolRouteBuilder.MapProtocol(handler);
    }

    public static WebApplicationBuilder AddAspNetCoreProtocolHost(
        this WebApplicationBuilder builder,
        Action<PacketTypeResolverOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddProtocolCore(configureOptions);
        builder.Services.AddSingleton<IWebSocketConnectionManager, WebSocketConnectionManager>();

        return builder;
    }
}
