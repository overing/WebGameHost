
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Core;

public static class ProtocolCoreExtensions
{
    public static IServiceCollection AddProtocolCore(
        this IServiceCollection collection,
        Action<PacketTypeResolverOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new PacketTypeResolverOptions();
        configureOptions.Invoke(options);

        collection.TryAddSingleton(options);
        collection.TryAddSingleton<IPacketTypeResolver, RegisteredPacketTypeResolver>();
        collection.TryAddSingleton<IPayloadSerializer, JsonPayloadSerializer>();
        collection.TryAddSingleton<IPacketEnvelopeCodec, JsonPacketEnvelopeCodec>();

        collection.AddSingleton<IProtocolRouteBuilder, ProtocolRouteBuilder>();
        collection.AddSingleton<IProtocolSessionFactory, WebSocketProtocolSessionFactory>();

        return collection;
    }
}
