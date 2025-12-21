
using System.Text.Json;

namespace ProtocolFramework.Core;

internal sealed record class PacketWithType
{
    public string TypeName { get; set; }
    public string PacketData { get; set; }

    public PacketWithType(string typeName, string packetData)
    {
        TypeName = typeName;
        PacketData = packetData;
    }
}

public interface IProtocolSession
{
    string SessionId { get; }
    Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default);
    ValueTask CloseAsync();
    IDictionary<string, object> Properties { get; }
}

public sealed class ProtocolSession(IProtocolConnection connection) : IProtocolSession, IDisposable
{
    private readonly IProtocolConnection _connection = connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public string SessionId { get; } = Guid.NewGuid().ToString();
    public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();

    public async Task SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        if (packet == null) throw new ArgumentNullException(nameof(packet));

        var data = JsonSerializer.SerializeToUtf8Bytes(packet);
        var pwt = new PacketWithType(packet.GetType().FullName!, Convert.ToBase64String(data));
        var payload = JsonSerializer.SerializeToUtf8Bytes(pwt);

        var buffer = new byte[sizeof(int) + payload.Length];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, sizeof(int)), payload.Length);
        payload.CopyTo(buffer.AsSpan(sizeof(int)));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            await _connection.WriteAsync(buffer, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
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
        GC.SuppressFinalize(this);
    }
}
