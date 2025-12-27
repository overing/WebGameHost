
using Microsoft.Extensions.DependencyInjection;

namespace ProtocolFramework.Core;

public sealed class ProtocolConnectionProcessor(IProtocolRouteBuilder protocolRouteBuilder)
{
    private readonly Lazy<IProtocolRoute> _route = new(() => protocolRouteBuilder.Build());

    public async Task ProcessPacketsAsync(
        IProtocolReader reader,
        IProtocolSession session,
        IServiceScopeFactory serviceScopeFactory,
        IProtocolErrorHandler? errorHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.SessionClosed);

        var token = linkedCts.Token;

        var route = _route.Value;

        while (!token.IsCancellationRequested)
        {
            ReadOnlyMemory<byte> packetData;
            try
            {
                packetData = await reader.ReadAsync(token).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (packetData.IsEmpty) continue;

            using var scope = serviceScopeFactory.CreateScope();
            try
            {
                await route
                    .InvokeAsync(packetData, session, scope.ServiceProvider, token)
                    .ConfigureAwait(ConfigureAwaitOptions.None);
            }
            catch (Exception ex)
            {
                if (errorHandler is { } handler)
                {
                    await handler
                        .OnHandlerExceptionAsync(ex, packetData, session)
                        .ConfigureAwait(continueOnCapturedContext: false);
                }
                else
                {
                    throw;
                }
            }
        }
    }
}
