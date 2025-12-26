using System.Buffers;
using System.IO.Pipelines;

namespace ProtocolFramework.Core;

public sealed class StreamProtocolConnection(Stream stream) : IProtocolConnection, IDisposable
{
    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly PipeReader _reader = PipeReader.Create(stream);
    private readonly CancellationTokenSource _cts = new();

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        await _stream.WriteAsync(source, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (TryReadPacket(ref buffer, out var packet))
            {
                _reader.AdvanceTo(buffer.Start);
                return packet;
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

        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask CloseAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _reader.CompleteAsync().ConfigureAwait(false);
        _stream.Close();
    }

    public void Dispose()
    {
        _cts.Dispose();
        (_stream as IDisposable)?.Dispose();
    }

    private static bool TryReadPacket(ref ReadOnlySequence<byte> buffer, out byte[] packet)
    {
        packet = [];

        if (buffer.Length < sizeof(int))
            return false;

        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        buffer.Slice(0, sizeof(int)).CopyTo(lengthBytes);
        var length = BitConverter.ToInt32(lengthBytes);

        if (length <= 0 || length > 10 * 1024 * 1024) // Max 10MB
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
