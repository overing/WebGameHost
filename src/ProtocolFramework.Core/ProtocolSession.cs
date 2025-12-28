using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Core;

public interface IProtocolSession : IDisposable, IAsyncDisposable
{
    string SessionId { get; }
    IDictionary<string, object> Properties { get; }
    CancellationToken SessionClosed { get; }
    void StartProcessReceive(CancellationToken cancellationToken = default);
    ValueTask SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default) where TPacket : class;
    ValueTask CloseAsync();
}

public sealed class ProtocolSessionOptions
{
    public int MaxQueueSize { get; init; } = 5000;
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class ProtocolSession : IProtocolSession
{
    private readonly ILogger _logger;
    private readonly IProtocolConnection _connection;
    private readonly IPacketEnvelopeCodec _codec;
    private readonly IProtocolRoute _route;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IProtocolErrorHandler? _errorHandler;
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly Channel<IMemoryOwner<byte>> _sendQueue;
    private readonly ProtocolSessionOptions _options;
    private readonly Task _sendLoopTask;
    private volatile Exception? _sendLoopException;
    private Task? _receiveTask;
    private bool _disposed;

    public string SessionId { get; } = Guid.NewGuid().ToString();
    public IDictionary<string, object> Properties { get; } = new ConcurrentDictionary<string, object>();
    public CancellationToken SessionClosed => _sessionCts.Token;

    public ProtocolSession(
        ILogger<ProtocolSession> logger,
        IProtocolConnection connection,
        IPacketEnvelopeCodec codec,
        IProtocolRoute route,
        IServiceScopeFactory serviceScopeFactory,
        IProtocolErrorHandler? errorHandler = null,
        ProtocolSessionOptions? options = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _serviceScopeFactory = serviceScopeFactory;
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _errorHandler = errorHandler;
        _options = options ?? new();

        _sendQueue = Channel.CreateBounded<IMemoryOwner<byte>>(new BoundedChannelOptions(_options.MaxQueueSize)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        _sendLoopTask = Task.Run(ProcessSendQueueAsync);
    }

    public void StartProcessReceive(CancellationToken cancellationToken = default)
    {
        if (_receiveTask != null)
            throw new InvalidOperationException("已啟動");

        _receiveTask = ProcessReceiveAsync(cancellationToken);
    }

    public Task RunAsync(CancellationToken cancellationToken = default) => ProcessReceiveAsync(cancellationToken);

    private async Task ProcessReceiveAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, SessionClosed);

        var token = linkedCts.Token;

        while (!token.IsCancellationRequested)
        {
            IMemoryOwner<byte> packetOwner;
            try
            {
                packetOwner = await _connection.ReadAsync(token).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (OperationCanceledException) when (SessionClosed.IsCancellationRequested)
            {
                _logger.LogSessionClosed(LogLevel.Debug, SessionId);
                break;
            }

            using (packetOwner)
            {
                if (packetOwner.Memory.IsEmpty) continue;

                using var scope = _serviceScopeFactory.CreateScope();
                try
                {
                    await _route
                        .InvokeAsync(packetOwner.Memory, this, scope.ServiceProvider, token)
                        .ConfigureAwait(ConfigureAwaitOptions.None);
                }
                catch (Exception ex)
                {
                    _logger.LogReceiveError(LogLevel.Error, ex, SessionId);

                    if (_errorHandler is { } handler)
                    {
                        await handler
                            .OnHandlerExceptionAsync(ex, packetOwner.Memory, this)
                            .ConfigureAwait(continueOnCapturedContext: false);
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }
    }

    public async ValueTask SendAsync<TPacket>(TPacket packet, CancellationToken cancellationToken = default)
        where TPacket : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(packet);

        if (_sessionCts.IsCancellationRequested) throw new OperationCanceledException();
        if (_sendLoopException is not null)
            throw new InvalidOperationException("Send loop has terminated", _sendLoopException);

        var owner = _codec.Encode(packet);
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _sessionCts.Token);
            var timeout = _options.SendTimeout;
            if (timeout != Timeout.InfiniteTimeSpan && timeout != TimeSpan.Zero)
            {
                linkedCts.CancelAfter(timeout);
            }

            await _sendQueue.Writer.WriteAsync(owner, linkedCts.Token).ConfigureAwait(continueOnCapturedContext: false);
            owner = null;
        }
        finally
        {
            owner?.Dispose();
        }
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
                while (_sendQueue.Reader.TryRead(out var payloadOwner))
                {
                    using (payloadOwner)
                    {
                        var payload = payloadOwner.Memory;
                        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(int) + payload.Length);
                        try
                        {
                            BitConverter.TryWriteBytes(buffer.AsSpan(0, sizeof(int)), payload.Length);
                            payload.Span.CopyTo(buffer.AsSpan(sizeof(int)));

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
            while (_sendQueue.Reader.TryRead(out var remaining))
            {
                remaining.Dispose();
            }
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
            var drainTask = Task.Delay(_options.DrainTimeout);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _sendQueue.Writer.TryComplete();

        await _sessionCts.CancelAsync().ConfigureAwait(ConfigureAwaitOptions.None);

#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            await _sendLoopTask.ConfigureAwait(ConfigureAwaitOptions.None);
        }
        catch { /* ignore */ }
#pragma warning restore CA1031 // Do not catch general exception types

        if (_receiveTask is { } receiveTask)
        {
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                await receiveTask.ConfigureAwait(ConfigureAwaitOptions.None);
            }
            catch { /* ignore */ }
#pragma warning restore CA1031 // Do not catch general exception types
        }

        while (_sendQueue.Reader.TryRead(out var remaining))
            remaining.Dispose();

        await _connection.CloseAsync().ConfigureAwait(continueOnCapturedContext: false);

        _sessionCts.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;

        _sendQueue.Writer.TryComplete();

        _sessionCts.Cancel();
#pragma warning disable CA1031 // Do not catch general exception types
        try
        {
            _sendLoopTask.GetAwaiter().GetResult();
        }
        catch { /* ignore */ }
#pragma warning restore CA1031 // Do not catch general exception types

        if (_receiveTask is { } receiveTask)
        {
#pragma warning disable CA1031 // Do not catch general exception types
            try
            {
                receiveTask.GetAwaiter().GetResult();
            }
            catch { /* ignore */ }
#pragma warning restore CA1031 // Do not catch general exception types
        }

        while (_sendQueue.Reader.TryRead(out var remaining))
            remaining.Dispose();

        _connection.CloseAsync().AsTask().GetAwaiter().GetResult();

        _sessionCts.Dispose();

        _disposed = true;
    }
}

internal static partial class ProtocolSessionLoggerExtensions
{
    [LoggerMessage("Session#{SessionId} Close")]
    public static partial void LogSessionClosed(this ILogger logger, LogLevel logLevel, string sessionId);

    [LoggerMessage("Session#{SessionId} Receive Error")]
    public static partial void LogReceiveError(this ILogger logger, LogLevel logLevel, Exception exception, string sessionId);
}

internal sealed class PooledMemoryOwner(byte[] array, int length) : IMemoryOwner<byte>
{
    private byte[]? _array = array;
    private readonly int _length = length;

    public Memory<byte> Memory
    {
        get
        {
            var arr = _array;
            ObjectDisposedException.ThrowIf(arr is null, this);
            return new Memory<byte>(arr, 0, _length);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _array, null) is { } arr)
        {
            ArrayPool<byte>.Shared.Return(arr);
        }
    }
}

public interface IProtocolSessionFactory
{
    Task<IProtocolSession> ConnectAsync(
        string address,
        int port,
        string token,
        Action<IProtocolRouteBuilder>? configRoute = null,
        CancellationToken cancellationToken = default);
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class WebSocketProtocolSessionFactory(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory serviceScopeFactory,
    IPacketEnvelopeCodec codec,
    IProtocolRouteBuilder routeBuilder,
    IProtocolErrorHandler? errorHandler = null)
    : IProtocolSessionFactory
{
    private readonly IServiceScopeFactory _serviceScopeFactory = serviceScopeFactory;
    private readonly IPacketEnvelopeCodec _codec = codec;
    private readonly IProtocolRouteBuilder _routeBuilder = routeBuilder;
    private readonly IProtocolErrorHandler? _errorHandler = errorHandler;

    public async Task<IProtocolSession> ConnectAsync(
        string address,
        int port,
        string token,
        Action<IProtocolRouteBuilder>? configRoute,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger<ProtocolSession>();

        var socket = default(ClientWebSocket?);
        var stream = default(Stream?);
        var connection = default(StreamProtocolConnection?);

        try
        {
            socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
            var wsUri = new Uri($"ws://{address}:{port}/ws?access_token={token}");
            await socket.ConnectAsync(wsUri, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

#pragma warning disable CA2000 // stream 所有權轉移給 connection，由 finally 統一處理
            stream = WebSocketStream.Create(socket, WebSocketMessageType.Binary, ownsWebSocket: true);
            connection = new StreamProtocolConnection(stream);
#pragma warning restore CA2000

            var builder = _routeBuilder;
            if (configRoute is { })
            {
                builder = builder.Clone();
                configRoute.Invoke(builder);
            }
            var route = builder.Build();

            var session = new ProtocolSession(
                logger,
                connection,
                _codec,
                route,
                _serviceScopeFactory,
                _errorHandler);
            session.StartProcessReceive(cancellationToken);

            socket = null;
            stream = null;
            connection = null;

            return session;
        }
        finally
        {
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            else if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);

            socket?.Dispose();
        }
    }
}
