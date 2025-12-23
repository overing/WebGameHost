
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
        if (collection == null)
            throw new ArgumentNullException(nameof(collection));

        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions));

        var options = new PacketTypeResolverOptions();
        configureOptions.Invoke(options);

        collection.TryAddSingleton(options);
        collection.TryAddSingleton<IPacketTypeResolver, RegisteredPacketTypeResolver>();
        collection.TryAddSingleton<IPayloadSerializer, JsonPayloadSerializer>();
        collection.TryAddSingleton<IPacketEnvelopeCodec, JsonPacketEnvelopeCodec>();

        collection.AddSingleton<IProtocolRouteBuilder, ProtocolRouteBuilder>();
        collection.AddSingleton<IProtocolClientFactory, ProtocolClientFactory>();

        return collection;
    }
}
