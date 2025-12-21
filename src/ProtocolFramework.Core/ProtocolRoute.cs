
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProtocolFramework.Core;

internal sealed record class ProtocolMeta
{
    public Type PacketType { get; set; }
    public Func<object, InvocationContext, Task> CompiledHandler { get; set; }

    public ProtocolMeta(Type packetType, Func<object, InvocationContext, Task> compiledHandler)
    {
        PacketType = packetType;
        CompiledHandler = compiledHandler;
    }
}

internal sealed class InvocationContext
{
    public IProtocolSession Session { get; set; } = default!;
    public IServiceProvider? ServiceProvider { get; set; }
    public CancellationToken CancellationToken { get; set; }
}

public interface IProtocolRouteBuilder
{
    ProtocolRoute Build();
    IProtocolRouteBuilder MapProtocol(Delegate handler);
    IProtocolRouteBuilder Clone();
}

internal sealed class ProtocolRouteBuilder : IProtocolRouteBuilder
{
    private readonly Dictionary<string, ProtocolMeta> _mapping;

    public ProtocolRouteBuilder()
        => _mapping = [];

    private ProtocolRouteBuilder(IDictionary<string, ProtocolMeta> mapping)
        => _mapping = new(mapping);

    public IProtocolRouteBuilder MapProtocol(Delegate handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        var methodInfo = handler.Method;
        var parameters = methodInfo.GetParameters();

        var packetParam = parameters.FirstOrDefault(p =>
            p.ParameterType.IsClass &&
            p.ParameterType != typeof(string) &&
            p.ParameterType != typeof(IProtocolSession) &&
            p.ParameterType != typeof(CancellationToken))
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

    public ProtocolRoute Build() => new(_mapping);

    public IProtocolRouteBuilder Clone() => new ProtocolRouteBuilder(_mapping);
}

public sealed class ProtocolRoute
{
    private readonly IReadOnlyDictionary<string, ProtocolMeta> _mapping;

    internal ProtocolRoute(IReadOnlyDictionary<string, ProtocolMeta> mapping)
    {
        _mapping = mapping;
    }

    public async Task InvokeAsync(
        byte[] data,
        IProtocolSession session,
        IServiceProvider? serviceProvider = null,
        CancellationToken cancellationToken = default)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        if (!root.TryGetProperty("TypeName", out var typeNameElement))
            return;

        var typeName = typeNameElement.GetString();
        if (string.IsNullOrEmpty(typeName) || !_mapping.TryGetValue(typeName, out var meta))
            return;

        if (!root.TryGetProperty("PacketData", out var packetElement))
            return;

        var stampBeginDeserialize = Stopwatch.GetTimestamp();
        var packetBytes = Convert.FromBase64String(packetElement.GetString()!);
        var packet = JsonSerializer.Deserialize(packetBytes, meta.PacketType)
            ?? throw new FormatException("Packet data not available");
        var elapsedDeserialize = TimeSpan.FromTicks(Stopwatch.GetTimestamp() - stampBeginDeserialize);

        var context = new InvocationContext
        {
            Session = session,
            ServiceProvider = serviceProvider,
            CancellationToken = cancellationToken
        };

        var stampBeginInvoke = Stopwatch.GetTimestamp();
        await meta.CompiledHandler(packet, context);
        var elapsedInvoke = TimeSpan.FromTicks(Stopwatch.GetTimestamp() - stampBeginInvoke);

        var logger = serviceProvider?.GetRequiredService<ILogger<ProtocolRoute>>();
        logger?.LogInformation($"{meta.PacketType}, session#{session.SessionId}, deserialize: {elapsedDeserialize.TotalMilliseconds:N3}ms, invoke: {elapsedInvoke.TotalMilliseconds:N3}ms");
    }
}
