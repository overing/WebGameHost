
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProtocolFramework.Core;
using WebGameHost.Core.Protocols;

var appBuilder = Host.CreateApplicationBuilder(args);

appBuilder.Logging.ClearProviders()
    .AddSimpleConsole();

appBuilder.Services.AddHostedService<MainService>();

var app = appBuilder.Build();

await app.RunAsync();

[SuppressMessage("Performance", "CA1812", Justification = "這個類別透過 DI 建立")]
internal sealed class MainService(ILogger<MainService> logger) : BackgroundService
{
    private ILogger _logger = logger;
    private ProtocolClient? _client;

    private TaskCompletionSource<bool>? _login;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _client = await ProtocolClient.ConnectAsync("120.0.0.1", 5100, route =>
        {
            route.MapProtocol(HandleAsync);
        }, cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_client is not { } client)
            return;

        _login = new TaskCompletionSource<bool>();
        await client.SendAsync(new LoginRequest("overing", "abc123"), stoppingToken);

        if (await _login.Task)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);

                await client.SendAsync(new EchoRequest(), stoppingToken);
            }
        }
    }

    private Task HandleAsync(LoginResponse response)
    {
        _logger.LogInformation($"Login: {response.Success}");
        _login!.SetResult(response.Success);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client is { } client)
            await client.DisconnectAsync();
    }
}
