
using System.IO.Pipelines;
using System.Net.Sockets;

namespace ProtocolFramework.Core;

public sealed class SocketProtocolConnection : IProtocolConnection, IDisposable
{
    private readonly Socket _socket;
    private readonly Pipe _pipe;
    private readonly Task _fillPipeTask;
    private readonly CancellationTokenSource _cts;

    public SocketProtocolConnection(Socket socket)
    {
        _socket = socket;
        _pipe = new Pipe();
        _cts = new CancellationTokenSource();

        _fillPipeTask = FillPipeAsync(_cts.Token);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        await _socket.SendAsync(source, SocketFlags.None, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <summary>
    /// 從 Pipe 讀取資料
    /// </summary>
    public async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _pipe.Reader.ReadAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return new(result.Buffer, result.IsCompleted);
    }

    /// <summary>
    /// 通知 Pipe 已消費位置
    /// </summary>
    public void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        _pipe.Reader.AdvanceTo(consumed, examined);
    }

    /// <summary>
    /// 從 Socket 讀取並填充到 Pipe
    /// </summary>
    private async Task FillPipeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var memory = _pipe.Writer.GetMemory(8192);
                var bytesRead = await _socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                if (bytesRead == 0)
                    break;

                _pipe.Writer.Advance(bytesRead);
                await _pipe.Writer.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
        catch (Exception)
        {
            // Socket 錯誤，完成 Pipe
        }
        finally
        {
            await _pipe.Writer.CompleteAsync().ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    public async ValueTask CloseAsync()
    {
        _cts.Cancel();
        await _fillPipeTask;
        _socket.Close();
        await _pipe.Reader.CompleteAsync().ConfigureAwait(continueOnCapturedContext: false);
    }

    public void Dispose()
    {
        _cts.Dispose();
        _socket.Dispose();
    }
}
