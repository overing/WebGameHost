
using System.Buffers;
using Microsoft.Extensions.DependencyInjection;

namespace ProtocolFramework.Core;

/// <summary>
/// 通用的封包處理器（可在 Core 中）
/// </summary>
public sealed class ProtocolConnectionProcessor(IProtocolRouteBuilder protocolRouteBuilder)
{
    private const int MaxPacketSize = 10 * 1024 * 1024;

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
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.SessionClosed);

        var token = linkedCts.Token;

        var route = _protocolRouteBuilder.Build();

        while (!token.IsCancellationRequested)
        {
            var result = await reader.ReadAsync(token).ConfigureAwait(continueOnCapturedContext: false);
            var buffer = result.Buffer;

            while (TryReadPacket(ref buffer, out var packetData))
            {
                using var scope = serviceScopeFactory.CreateScope();
                try
                {
                    await route.InvokeAsync(packetData, session, scope.ServiceProvider, token).ConfigureAwait(continueOnCapturedContext: false);
                }
                catch (Exception)
                {
                    // 錯誤處理由外部提供（通過回調或事件）
                    throw;
                }
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
                break;
        }
    }

    private static bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out byte[] packet)
    {
        packet = [];

        if (buffer.Length < sizeof(int))
            return false;

        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        buffer.Slice(0, sizeof(int)).CopyTo(lengthBytes);
        var length = BitConverter.ToInt32(lengthBytes);

        if (length <= 0 || length > MaxPacketSize)
            throw new InvalidDataException($"Invalid packet length: {length}");

        var totalLength = sizeof(int) + length;
        if (buffer.Length < totalLength)
            return false;

        packet = new byte[length];
        buffer.Slice(sizeof(int), length).CopyTo(packet);

        buffer = buffer.Slice(totalLength);
        return true;
    }
}

