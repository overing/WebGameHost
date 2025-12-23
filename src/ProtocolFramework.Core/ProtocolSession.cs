
using System.Text.Json;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Core;

internal sealed record class PacketEnvelope
{
    public string TypeName { get; set; } = default!;
    public JsonElement Packet { get; set; }
}

public interface IProtocolSession
{
    string SessionId { get; }
    Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class;
    ValueTask CloseAsync();
    IDictionary<string, object> Properties { get; }
}

public sealed class ProtocolSession(IProtocolConnection connection, IPacketEnvelopeCodec codec) : IProtocolSession, IDisposable
{
    private readonly IProtocolConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    private readonly IPacketEnvelopeCodec _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string SessionId { get; } = Guid.NewGuid().ToString();
    public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();

    public async Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        if (packet == null) throw new ArgumentNullException(nameof(packet));

        var payload = _codec.Encode(packet);

        var buffer = new byte[sizeof(int) + payload.Length];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, sizeof(int)), payload.Length);
        payload.CopyTo(buffer.AsSpan(sizeof(int)));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _connection.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask CloseAsync()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        return _connection.CloseAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _writeLock.Dispose();
        _disposed = true;
    }
}
