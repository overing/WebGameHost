
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProtocolFramework.Core;
using ProtocolFramework.Core.Serialization;

namespace ProtocolFramework.Host;

public static class AspNetCoreProtocolHostExtensions
{
    public static IProtocolRouteBuilder MapProtocol(this IHost host, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(host);

        var protocolRouteBuilder = host.Services.GetService<IProtocolRouteBuilder>()
            ?? throw new InvalidOperationException($"Require {nameof(AddAspNetCoreProtocolHost)}");
        return protocolRouteBuilder.MapProtocol(handler);
    }

    public static WebApplicationBuilder AddAspNetCoreProtocolHost(
        this WebApplicationBuilder builder,
        Action<PacketTypeResolverOptions> configureOptions,
        int port = 5100)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddProtocolCore(configureOptions);
        builder.Services.AddSingleton<AspNetCoreProtocolConnectionHandler>();

        builder.WebHost.UseKestrel(ConfigKestrel);

        return builder;

        void ConfigKestrel(KestrelServerOptions options) => options.ListenAnyIP(port, ConfigListener);

        static void ConfigListener(ListenOptions options)
            => options.UseConnectionHandler<AspNetCoreProtocolConnectionHandler>();
    }
}
