using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Core;

public interface IProtocolSession
{
    string SessionId { get; }
    CancellationToken SessionClosed { get; }
    ValueTask SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class;
    ValueTask CloseAsync();
    IDictionary<string, object> Properties { get; }
}

public sealed class ProtocolSession : IProtocolSession, IDisposable
{
    private readonly IProtocolConnection _connection;
    private readonly IPacketEnvelopeCodec _codec;
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly Channel<byte[]> _sendQueue;
    private readonly Task _sendLoopTask;
    private volatile Exception? _sendLoopException;
    private bool _disposed;
    
    public ProtocolSessionOptions Options { get; }

    public string SessionId { get; } = Guid.NewGuid().ToString();
    public IDictionary<string, object> Properties { get; } = new ConcurrentDictionary<string, object>();
    public CancellationToken SessionClosed => _sessionCts.Token;

    public ProtocolSession(IProtocolConnection connection, IPacketEnvelopeCodec codec, ProtocolSessionOptions? options = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        Options = options ?? new ProtocolSessionOptions();

        var channelOptions = new BoundedChannelOptions(Options.MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _sendQueue = Channel.CreateBounded<byte[]>(channelOptions);
        
        _sendLoopTask = Task.Run(ProcessSendQueueAsync);
    }

    public async ValueTask SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(packet);

        if (_sessionCts.IsCancellationRequested) throw new OperationCanceledException();

        if (_sendLoopException is not null)
        {
            throw new InvalidOperationException("Send loop has terminated", _sendLoopException);
        }

        var payload = _codec.Encode(packet);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCts.Token);
        if (Options.SendTimeout != Timeout.InfiniteTimeSpan && Options.SendTimeout != TimeSpan.Zero)
        {
            linkedCts.CancelAfter(Options.SendTimeout);
        }

        await _sendQueue.Writer.WriteAsync(payload, linkedCts.Token).ConfigureAwait(false);
    }

    private async Task ProcessSendQueueAsync()
    {
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            while (await _sendQueue.Reader
                .WaitToReadAsync(_sessionCts.Token)
                .ConfigureAwait(continueOnCapturedContext: false))
            {
                while (_sendQueue.Reader.TryRead(out var payload))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(sizeof(int) + payload.Length);
                    try
                    {
                        BitConverter.TryWriteBytes(buffer.AsSpan(0, sizeof(int)), payload.Length);
                        payload.CopyTo(buffer.AsSpan(sizeof(int)));

                        await _connection
                            .WriteAsync(buffer.AsMemory(0, sizeof(int) + payload.Length), _sessionCts.Token)
                            .ConfigureAwait(continueOnCapturedContext: false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _sendLoopException = ex;
            _sendQueue.Writer.TryComplete(ex);
        }
        finally
        {
            _sendQueue.Writer.TryComplete();
        }
#pragma warning restore CA1031 // Do not catch general exception types
    }

    public ValueTask CloseAsync() => CloseAndDrainAsync(drain: false);

    public async ValueTask CloseAndDrainAsync(bool drain = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Stop accepting new messages
        _sendQueue.Writer.TryComplete();

        if (drain)
        {
             var drainTask = Task.Delay(Options.DrainTimeout);
             var loopTask = _sendLoopTask;
             
             await Task.WhenAny(loopTask, drainTask).ConfigureAwait(ConfigureAwaitOptions.None);
        }

        await _sessionCts.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);

#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            await _sendLoopTask.ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (Exception)
        {
            // Ignore errors
        }
#pragma warning restore CA1031 // Do not catch general exception types

        await _connection.CloseAsync().ConfigureAwait(continueOnCapturedContext: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _sessionCts.Cancel();
        _sessionCts.Dispose();
        _disposed = true;
    }
}
