
using Microsoft.Extensions.DependencyInjection;

namespace ProtocolFramework.Core;

public static class ProtocolCoreExtensions
{
    public static IServiceCollection AddProtocolCore(this IServiceCollection collection)
    {
        if (collection == null)
            throw new ArgumentNullException(nameof(collection));

        collection.AddSingleton<IProtocolRouteBuilder, ProtocolRouteBuilder>();
        collection.AddSingleton<IProtocolClientFactory, ProtocolClientFactory>();

        return collection;
    }
}
