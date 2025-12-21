
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProtocolFramework.Core;

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

    public static WebApplicationBuilder AddAspNetCoreProtocolHost(this WebApplicationBuilder builder, int port = 5100)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddProtocolCore();
        builder.Services.AddSingleton<AspNetCoreProtocolConnectionHandler>();

        builder.WebHost.UseKestrel(ConfigKestrel);

        return builder;

        void ConfigKestrel(KestrelServerOptions options) => options.ListenAnyIP(port, ConfigListener);

        static void ConfigListener(ListenOptions options)
            => options.UseConnectionHandler<AspNetCoreProtocolConnectionHandler>();
    }
}
