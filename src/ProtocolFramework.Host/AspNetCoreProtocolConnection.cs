
using Microsoft.AspNetCore.Connections;
using ProtocolFramework.Core;

namespace ProtocolFramework.Host;

internal sealed class AspNetCoreProtocolConnection(ConnectionContext connection) : IProtocolConnection
{
    private readonly ConnectionContext _connection = connection;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        await _connection.Transport.Output.WriteAsync(source, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    public async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _connection.Transport.Input.ReadAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return new ReadResult(result.Buffer, result.IsCompleted);
    }

    public void AdvanceTo(SequencePosition consumed, SequencePosition examined)
        => _connection.Transport.Input.AdvanceTo(consumed, examined);

    public ValueTask CloseAsync() => _connection.Transport.Output.CompleteAsync();
}
