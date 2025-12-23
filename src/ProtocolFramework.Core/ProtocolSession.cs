
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Core;

public interface IProtocolSession
{
    string SessionId { get; }
    CancellationToken SessionClosed { get; }
    Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class;
    ValueTask CloseAsync();
    IDictionary<string, object> Properties { get; }
}

public sealed class ProtocolSession(IProtocolConnection connection, IPacketEnvelopeCodec codec) : IProtocolSession, IDisposable
{
    private readonly IProtocolConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly IPacketEnvelopeCodec _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _sessionCts = new();
    private bool _disposed;

    public string SessionId { get; } = Guid.NewGuid().ToString();
    public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();
    public CancellationToken SessionClosed => _sessionCts.Token;

    public async Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        if (packet == null) throw new ArgumentNullException(nameof(packet));

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCts.Token);

        var payload = _codec.Encode(packet);

        var buffer = new byte[sizeof(int) + payload.Length];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, sizeof(int)), payload.Length);
        payload.CopyTo(buffer.AsSpan(sizeof(int)));

        await _writeLock.WaitAsync(linkedCts.Token).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            await _connection.WriteAsync(buffer, linkedCts.Token).ConfigureAwait(continueOnCapturedContext: false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask CloseAsync()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        _sessionCts.Cancel();
        await _connection.CloseAsync().ConfigureAwait(continueOnCapturedContext: false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _sessionCts.Cancel();
        _sessionCts.Dispose();
        _writeLock.Dispose();
        _disposed = true;
    }
}
