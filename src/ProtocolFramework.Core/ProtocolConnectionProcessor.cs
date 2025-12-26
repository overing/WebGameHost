using System.Buffers;
using Microsoft.Extensions.DependencyInjection;

namespace ProtocolFramework.Core;

/// <summary>
/// 通用的封包處理器（可在 Core 中）
/// </summary>
public sealed class ProtocolConnectionProcessor(IProtocolRouteBuilder protocolRouteBuilder)
{
    private readonly IProtocolRouteBuilder _protocolRouteBuilder = protocolRouteBuilder;

    /// <summary>
    /// 通用的封包處理邏輯 - 完全基於抽象介面
    /// </summary>
    public async Task ProcessPacketsAsync(
        IProtocolReader reader,
        IProtocolSession session,
        IServiceScopeFactory serviceScopeFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.SessionClosed);

        var token = linkedCts.Token;

        var route = _protocolRouteBuilder.Build();

        while (!token.IsCancellationRequested)
        {
            ReadOnlyMemory<byte> packetData;
            try
            {
                packetData = await reader.ReadAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (packetData.IsEmpty) continue;

            using var scope = serviceScopeFactory.CreateScope();
            try
            {
                await route.InvokeAsync(packetData.ToArray(), session, scope.ServiceProvider, token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 錯誤處理由外部提供（通過回調或事件）
                throw;
            }
        }
    }
}
