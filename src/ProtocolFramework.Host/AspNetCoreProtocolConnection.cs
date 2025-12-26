
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
    IServiceScopeFactory serviceScopeFactory,
    IProtocolRouteBuilder routeBuilder,
    IPacketEnvelopeCodec codec)
    : IWebSocketConnectionManager
{
    private readonly ConcurrentDictionary<string, IProtocolSession> _connections = new();
    private readonly ProtocolConnectionProcessor _processor = new(routeBuilder);
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly IPacketEnvelopeCodec _codec = codec;

    public async Task HandleConnectionAsync(HttpContext httpContext, string userId, WebSocket webSocket)
    {
        using var stream = WebSocketStream.Create(webSocket, WebSocketMessageType.Binary, ownsWebSocket: true);
        using var connection = new StreamProtocolConnection(stream);
        using var session = new ProtocolSession(connection, _codec);

        _connections[userId] = session;

        try
        {
            await _processor.ProcessPacketsAsync(connection, session, _serviceScopeFactory, httpContext.RequestAborted).ConfigureAwait(ConfigureAwaitOptions.None);
        }
        finally
        {
            _connections.TryRemove(userId, out _);
            Console.WriteLine($"User {userId} disconnected");
        }
    }

    public async Task BroadcastAsync<TPacket>(TPacket packet) where TPacket : class
    {
        foreach (var (_, session) in _connections)
        {
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                await session.SendAsync(packet).ConfigureAwait(ConfigureAwaitOptions.None);
            }
            catch
            {
                // Ignore errors
            }
#pragma warning restore CA1031 // Do not catch general exception types
        }
    }
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class AspNetCoreProtocolConnectionHandler(
    RequestDelegate next,
    ILogger<AspNetCoreProtocolConnectionHandler> logger,
    IPacketEnvelopeCodec packetEnvelopeCodec,
    IProtocolRouteBuilder routeBuilder,
    IServiceScopeFactory serviceScopeFactory)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger _logger = logger;
    private readonly IPacketEnvelopeCodec _packetEnvelopeCodec = packetEnvelopeCodec;
    private readonly ProtocolConnectionProcessor _processor = new ProtocolConnectionProcessor(routeBuilder);
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;

    [SuppressMessage("Design", "CA1031", Justification = "Unknown framework user errors")]
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await _next(context).ConfigureAwait(ConfigureAwaitOptions.None);
            return;
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(ConfigureAwaitOptions.None);

        using var stream = WebSocketStream.Create(webSocket, WebSocketMessageType.Binary, ownsWebSocket: true);
        using var connection = new StreamProtocolConnection(stream);
        using var session = new ProtocolSession(connection, _packetEnvelopeCodec);

        _logger.LogClientConnected(LogLevel.Information, session.SessionId);

        try
        {
            var ct = context.RequestAborted;

            await _processor
                .ProcessPacketsAsync(connection, session, _serviceScopeFactory, ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);
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

    [LoggerMessage("Client disconnected: {SessionId}")]
    public static partial void LogClientDisconnected(this ILogger logger, LogLevel logLevel, string sessionId);
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

    public static IEndpointConventionBuilder MapProtocolWebSocket(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var app = endpoints.CreateApplicationBuilder();
        app.UseWebSockets();
        app.UseMiddleware<AspNetCoreProtocolConnectionHandler>();

        var pipeline = app.Build();
        return endpoints.Map(pattern, pipeline);
    }
}
