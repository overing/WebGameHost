
using System.Buffers;

namespace ProtocolFramework.Core;

/// <summary>
/// 讀取結果
/// </summary>
public readonly struct ReadResult(ReadOnlySequence<byte> buffer, bool isCompleted)
{
    public ReadOnlySequence<byte> Buffer { get; } = buffer;
    public bool IsCompleted { get; } = isCompleted;
}

/// <summary>
/// 協定資料讀取器
/// </summary>
public interface IProtocolReader
{
    ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default);
    void AdvanceTo(SequencePosition consumed, SequencePosition examined);
}
