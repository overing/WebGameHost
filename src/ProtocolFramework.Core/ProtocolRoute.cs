
using System.Collections.Frozen;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Core;

internal sealed record class ProtocolMeta
{
    public Type PacketType { get; }
    public Func<object, InvocationContext, Task> CompiledHandler { get; }

    public ProtocolMeta(Type packetType, Func<object, InvocationContext, Task> compiledHandler)
    {
        PacketType = packetType;
        CompiledHandler = compiledHandler;
    }
}

internal readonly record struct InvocationContext(
    IProtocolSession Session,
    IServiceProvider ServiceProvider,
    CancellationToken CancellationToken);

public interface IProtocolRouteBuilder
{
    ProtocolRoute Build();
    IProtocolRouteBuilder MapProtocol(Delegate handler);
    IProtocolRouteBuilder Clone();
}

internal sealed class ProtocolRouteBuilder : IProtocolRouteBuilder
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IPacketTypeResolver _packetTypeResolver;
    private readonly Dictionary<string, ProtocolMeta> _mapping;

    public ProtocolRouteBuilder(IServiceProvider serviceProvider)
        : this(serviceProvider, new Dictionary<string, ProtocolMeta>(StringComparer.Ordinal)) { }

    private ProtocolRouteBuilder(IServiceProvider serviceProvider, IDictionary<string, ProtocolMeta> mapping)
    {
        _serviceProvider = serviceProvider;
        _packetTypeResolver = serviceProvider.GetRequiredService<IPacketTypeResolver>();
        _mapping = new(mapping);
    }

    public IProtocolRouteBuilder MapProtocol(Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var methodInfo = handler.Method;
        var parameters = methodInfo.GetParameters();

        var packetParam = parameters.SingleOrDefault(p =>
            p.ParameterType.IsClass &&
            !p.ParameterType.IsAbstract &&
            p.ParameterType != typeof(string) &&
            p.ParameterType != typeof(IProtocolSession) &&
            p.ParameterType != typeof(CancellationToken) &&
            _packetTypeResolver.TryGetTypeName(p.ParameterType, out _))
            ?? throw new ArgumentException("Handler must have at least one packet parameter");

        var packetType = packetParam.ParameterType;
        var typeName = packetType.FullName
            ?? throw new ArgumentException("Packet type must have a FullName");

        if (_mapping.ContainsKey(typeName))
            throw new InvalidOperationException($"Handler for {typeName} already registered");

        var compiledHandler = CompileHandler(handler, methodInfo, parameters, packetParam);
        _mapping.Add(typeName, new ProtocolMeta(packetType, compiledHandler));

        return this;
    }

    private Func<object, InvocationContext, Task> CompileHandler(
        Delegate handler,
        MethodInfo methodInfo,
        ParameterInfo[] parameters,
        ParameterInfo packetParam)
    {
        var packetParameter = Expression.Parameter(typeof(object), "packet");
        var contextParameter = Expression.Parameter(typeof(InvocationContext), "context");

        var arguments = new Expression[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var paramType = param.ParameterType;

            if (param == packetParam)
            {
                arguments[i] = Expression.Convert(packetParameter, paramType);
            }
            else if (paramType == typeof(IProtocolSession))
            {
                arguments[i] = Expression.Property(contextParameter, nameof(InvocationContext.Session));
            }
            else if (paramType == typeof(CancellationToken))
            {
                arguments[i] = Expression.Property(contextParameter, nameof(InvocationContext.CancellationToken));
            }
            else if (!paramType.IsValueType)
            {
                var serviceProviderProperty = Expression.Property(contextParameter, nameof(InvocationContext.ServiceProvider));

                var getServiceMethod = typeof(IServiceProvider).GetMethod(nameof(IServiceProvider.GetService));

                Expression serviceExpression;
                if (getServiceMethod != null)
                {
                    var getServiceCall = Expression.Call(
                        serviceProviderProperty,
                        getServiceMethod,
                        Expression.Constant(paramType));
                    serviceExpression = Expression.Convert(getServiceCall, paramType);
                }
                else
                {
                    serviceExpression = Expression.Constant(null, paramType);
                }

                var nullCheck = Expression.Condition(
                    Expression.Equal(serviceProviderProperty, Expression.Constant(null)),
                    Expression.Constant(null, paramType),
                    serviceExpression);

                arguments[i] = nullCheck;
            }
            else
            {
                arguments[i] = Expression.Default(paramType);
            }
        }

        Expression callExpression = handler.Target is null
            ? Expression.Invoke(Expression.Constant(handler), arguments)
            : Expression.Call(Expression.Constant(handler.Target), methodInfo, arguments);

        Expression<Func<object, InvocationContext, Task>> lambda;

        if (methodInfo.ReturnType == typeof(Task))
        {
            lambda = Expression.Lambda<Func<object, InvocationContext, Task>>(
                callExpression, packetParameter, contextParameter);
        }
        else if (methodInfo.ReturnType == typeof(void))
        {
            var completedTask = Expression.Property(null, typeof(Task).GetProperty(nameof(Task.CompletedTask))!);
            lambda = Expression.Lambda<Func<object, InvocationContext, Task>>(
                Expression.Block(callExpression, completedTask),
                packetParameter, contextParameter);
        }
        else if (methodInfo.ReturnType.IsGenericType &&
                 methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            lambda = Expression.Lambda<Func<object, InvocationContext, Task>>(
                Expression.Convert(callExpression, typeof(Task)),
                packetParameter, contextParameter);
        }
        else
        {
            throw new ArgumentException($"Unsupported return type: {methodInfo.ReturnType}");
        }

        return lambda.Compile();
    }

    public ProtocolRoute Build()
    {
        var logger = _serviceProvider.GetRequiredService<ILogger<ProtocolRoute>>();
        var codec = _serviceProvider.GetRequiredService<IPacketEnvelopeCodec>();
        var serializer = _serviceProvider.GetRequiredService<IPayloadSerializer>();
        return new(logger, codec, serializer, _mapping.ToFrozenDictionary());
    }

    public IProtocolRouteBuilder Clone() => new ProtocolRouteBuilder(_serviceProvider, _mapping);
}

public sealed class ProtocolRoute
{
    private readonly ILogger<ProtocolRoute> _logger;
    private readonly FrozenDictionary<string, ProtocolMeta> _mapping;
    private readonly IPacketEnvelopeCodec _codec;
    private readonly IPayloadSerializer _serializer;

    internal ProtocolRoute(
        ILogger<ProtocolRoute> logger,
        IPacketEnvelopeCodec codec,
        IPayloadSerializer serializer,
        FrozenDictionary<string, ProtocolMeta> mapping)
    {
        _logger = logger;
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _mapping = mapping;
    }

    public async Task InvokeAsync(
        ReadOnlyMemory<byte> data,
        IProtocolSession session,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var envelope = _codec.Decode(data);

        if (!_mapping.TryGetValue(envelope.TypeName, out var meta))
        {
            _logger.LogProtocolUnhandle(LogLevel.Warning, envelope.TypeName, session.SessionId);
            return;
        }

        var stampBeginDeserialize = Stopwatch.GetTimestamp();
        var packet = _serializer.Deserialize(envelope.PayloadSpan, meta.PacketType);
        var elapsedDeserialize = TimeSpan.FromTicks(Stopwatch.GetTimestamp() - stampBeginDeserialize);

        var context = new InvocationContext
        {
            Session = session,
            ServiceProvider = serviceProvider,
            CancellationToken = cancellationToken
        };

        var stampBeginInvoke = Stopwatch.GetTimestamp();
        await meta.CompiledHandler(packet, context).ConfigureAwait(false);
        var elapsedInvoke = TimeSpan.FromTicks(Stopwatch.GetTimestamp() - stampBeginInvoke);

        _logger.LogProtocolElapsed(LogLevel.Information, meta.PacketType, session.SessionId, elapsedDeserialize.TotalMilliseconds, elapsedInvoke.TotalMilliseconds);
    }
}

internal static partial class ProtocolRouteLoggerExtensions
{
    [LoggerMessage("{TypeName}, session#{SessionId}, unhandle")]
    public static partial void LogProtocolUnhandle(this ILogger logger, LogLevel logLevel, string typeName, string sessionId);

    [LoggerMessage("{PacketType}, session#{SessionId}, deserialize: {ElapsedDeserializeMs:N3}ms, invoke: {ElapsedInvokeMs:N3}ms")]
    public static partial void LogProtocolElapsed(this ILogger logger, LogLevel logLevel, Type packetType, string sessionId, double elapsedDeserializeMs, double elapsedInvokeMs);
}
