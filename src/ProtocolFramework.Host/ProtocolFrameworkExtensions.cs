
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProtocolFramework.Core;

namespace ProtocolFramework.Host;

public static class ProtocolFrameworkExtensions
{
    public static ProtocolRouteBuilder MapProtocol(this IHost host, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(host);

        var protocolRouteBuilder = host.Services.GetService<ProtocolRouteBuilder>()
            ?? throw new InvalidOperationException($"Require {nameof(AddProtocolFramework)}");
        return protocolRouteBuilder.MapProtocol(handler);
    }

    public static WebApplicationBuilder AddProtocolFramework(this WebApplicationBuilder builder, int port = 5100)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<ProtocolRouteBuilder>();
        builder.Services.AddSingleton<AspNetCoreProtocolConnectionHandler>();

        builder.WebHost.UseKestrel(options =>
        {
            options.ListenAnyIP(port, listenOptions =>
            {
                listenOptions.UseConnectionHandler<AspNetCoreProtocolConnectionHandler>();
            });
        });
        return builder;
    }
}
