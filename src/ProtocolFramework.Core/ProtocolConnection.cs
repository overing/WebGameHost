
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace ProtocolFramework.Core;

/// <summary>
/// 完整的協定連線（組合讀寫）
/// </summary>
public interface IProtocolConnection : IProtocolReader, IProtocolWriter
{
    ValueTask CloseAsync();
}

/// <summary>
/// 協定資料讀取器
/// </summary>
public interface IProtocolReader
{
    /// <summary>
    /// 讀取完整封包
    /// </summary>
    ValueTask<IMemoryOwner<byte>> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 協定資料寫入器
/// </summary>
public interface IProtocolWriter
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default);
}

public sealed class StreamProtocolConnection(Stream stream) : IProtocolConnection, IDisposable
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly PipeReader _reader = PipeReader.Create(stream);
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        await _stream.WriteAsync(source, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    public async ValueTask<IMemoryOwner<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReadResult result;
            try
            {
                result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return EmptyMemoryOwner.Instance;
            }
            catch (Exception ex) when (IsConnectionClosed(ex))
            {
                return EmptyMemoryOwner.Instance;
            }

            var buffer = result.Buffer;

            IMemoryOwner<byte>? packetOwner = null;
            try
            {
                if (TryReadPacket(ref buffer, out packetOwner))
                {
                    _reader.AdvanceTo(buffer.Start);
                    var owner = packetOwner;
                    packetOwner = null;
                    return owner;
                }
            }
            finally
            {
                packetOwner?.Dispose();
            }

            if (result.IsCompleted)
            {
                if (!buffer.IsEmpty)
                {
                    throw new InvalidOperationException("Incomplete message");
                }
                break;
            }

            _reader.AdvanceTo(buffer.Start, buffer.End);
        }

        return EmptyMemoryOwner.Instance;
    }

    private static bool IsConnectionClosed(Exception ex)
        => ex is IOException or SocketException or WebSocketException
            || ex.InnerException is not null && IsConnectionClosed(ex.InnerException);

    public async ValueTask CloseAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.None);
        await _reader.CompleteAsync().ConfigureAwait(continueOnCapturedContext: false);
        _stream.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.None);
        await _reader.CompleteAsync().ConfigureAwait(continueOnCapturedContext: false);
        await _stream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        _cts.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _reader.Complete();
        _stream.Dispose();
        _cts.Dispose();
    }

    private static bool TryReadPacket(
        ref ReadOnlySequence<byte> buffer,
        [NotNullWhen(true)] out IMemoryOwner<byte>? packetOwner)
    {
        packetOwner = null;

        if (buffer.Length < sizeof(int))
            return false;

        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        buffer.Slice(0, sizeof(int)).CopyTo(lengthBytes);
        var length = BitConverter.ToInt32(lengthBytes);

        if (length <= 0 || length > 10 * 1024 * 1024) // Max 10MB
        {
            throw new InvalidDataException($"Invalid packet length: {length}");
        }

        var totalLength = sizeof(int) + length;
        if (buffer.Length < totalLength)
            return false;

        var array = ArrayPool<byte>.Shared.Rent(length);
        var owner = new PooledMemoryOwner(array, length);

        try
        {
            buffer.Slice(sizeof(int), length).CopyTo(array.AsSpan(0, length));
        }
        catch
        {
            owner.Dispose();
            throw;
        }

        packetOwner = owner;
        buffer = buffer.Slice(totalLength);
        return true;
    }

    private sealed class EmptyMemoryOwner : IMemoryOwner<byte>
    {
        public static readonly EmptyMemoryOwner Instance = new();
        public Memory<byte> Memory => Memory<byte>.Empty;
        public void Dispose() { }
    }
}
