
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProtocolFramework.Core;
using WebGame.Core.Protocols;

var appBuilder = Host.CreateApplicationBuilder(args);

appBuilder.Logging.ClearProviders()
    .AddSimpleConsole();

appBuilder.Services
    .AddHostedService<MainService>()
    .AddOptions<MainServiceOptions>()
    .Configure(appBuilder.Configuration.GetSection(nameof(MainServiceOptions)).Bind)
    .ValidateDataAnnotations()
    .ValidateOnStart();

var app = appBuilder.Build();

await app.RunAsync();

[SuppressMessage("Performance", "CA1812", Justification = "這個類別透過 DI 建立")]
internal sealed record class MainServiceOptions
{
    [HostAddress]
    [Required(ErrorMessage = "為必要項目")]
    public required string Host { get; init; }

    [Range(minimum: 2048, maximum: 65565, ErrorMessage = "容許範圍 2048 ~ 65535")]
    [Required(ErrorMessage = "為必要項目")]
    public required int Port { get; init; }
}

[SuppressMessage("Performance", "CA1812", Justification = "這個類別透過 DI 建立")]
internal sealed class MainService(
    ILogger<MainService> logger,
    ILoggerFactory loggerFactory,
    IOptions<MainServiceOptions> options)
    : BackgroundService
{
    private readonly MainServiceOptions _options = options.Value;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger _logger = logger;

    private ProtocolClient? _client;

    private TaskCompletionSource<bool>? _login;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _client = await ProtocolClient.ConnectAsync(_loggerFactory, _options.Host, _options.Port, BindProtocolHandle);
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

    private void BindProtocolHandle(ProtocolRouteBuilder routeBuilder)
    {
        routeBuilder.MapProtocol(HandleLoginResponseAsync);
        routeBuilder.MapProtocol(HandleEchoResponseAsync);
    }

    private Task HandleLoginResponseAsync(LoginResponse response)
    {
        _logger.LogLogin(LogLevel.Information, response.Success);
        _login!.SetResult(response.Success);
        return Task.CompletedTask;
    }

    private async Task HandleEchoResponseAsync(IProtocolSession session, EchoResponse _, CancellationToken cancellationToken)
    {
        await Task.Delay(1000, cancellationToken);
        await session.SendAsync(new EchoRequest(), cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client is { } client)
            await client.DisconnectAsync();
    }

    public override void Dispose()
    {
        _loggerFactory.Dispose();
        base.Dispose();
    }
}

internal static partial class MainServiceLoggerExtensions
{
    [LoggerMessage("Login: {Result}")]
    public static partial void LogLogin(this ILogger logger, LogLevel logLevel, bool result);
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
internal sealed class HostAddressAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string host || string.IsNullOrWhiteSpace(host)) return ValidationResult.Success;

        if (IPAddress.TryParse(host, out _)) return ValidationResult.Success;

        if (Uri.CheckHostName(host) == UriHostNameType.Dns) return ValidationResult.Success;

        return new ValidationResult(ErrorMessage ?? "Host 必須是有效的 Domain Name 或 IP 位址");
    }
}