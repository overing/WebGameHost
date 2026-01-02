
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
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
    .AddOptions<ProgramOptions>()
    .Configure(appBuilder.Configuration.GetSection(nameof(ProgramOptions)).Bind)
    .ValidateDataAnnotations()
    .ValidateOnStart();

appBuilder.Services.AddProtocolCore(options =>
{
    options.RegisterAssemblyOf<EchoRequest>();
});

var app = appBuilder.Build();

var routeBuilder = app.Services.GetRequiredService<IProtocolRouteBuilder>();

routeBuilder.MapProtocol((IProtocolSession session, EchoResponse _, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    // logger.LogEcho(LogLevel.Information);
    return Task.CompletedTask;
});

app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(async () =>
{
    var options = app.Services.GetRequiredService<IOptions<ProgramOptions>>().Value;
    var loginUri = new Uri($"http://{options.Host}:{options.Port}/api/login");
    using var loginContent = JsonContent.Create(new { account = "player1", password = "password" });
    using var httpClient = new HttpClient();
    var response = await httpClient.PostAsync(loginUri, loginContent, default).ConfigureAwait(ConfigureAwaitOptions.None);
    response.EnsureSuccessStatusCode();
    var jsonObject = await response.Content.ReadFromJsonAsync<JsonObject>(default);
    var accessToken = jsonObject!["accessToken"]!.GetValue<string>();
    var refreshToken = jsonObject!["refreshToken"]!.GetValue<string>();

    var sessionFactory = app.Services.GetRequiredService<IProtocolSessionFactory>();
    var session = await sessionFactory
        .ConnectAsync(options.Host, options.Port, accessToken)
        .ConfigureAwait(ConfigureAwaitOptions.None);

    if (session.Properties.TryGetValue("Echo", out var exists))
    {
        var typed = ((CancellationTokenSource cts, Task task))exists;
        await typed.cts.CancelAsync();
        try { await typed.task.ConfigureAwait(continueOnCapturedContext: false); } finally { }
    }

    var cts = CancellationTokenSource.CreateLinkedTokenSource(session.SessionClosed);
#pragma warning disable CA2025 // Do not pass 'IDisposable' instances into unawaited tasks
    session.Properties["Echo"] = (cts, EchoAsync(session, cts.Token));
#pragma warning restore CA2025 // Do not pass 'IDisposable' instances into unawaited tasks
});

await app.RunAsync();

static async Task EchoAsync(IProtocolSession session, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(1000, cancellationToken);
        await session.SendAsync(new EchoRequest(), cancellationToken);
    }
}

[SuppressMessage("Performance", "CA1812", Justification = "這個類別透過 DI 建立")]
internal sealed record class ProgramOptions
{
    [HostAddress]
    [Required(ErrorMessage = "為必要項目")]
    public required string Host { get; init; }

    [Range(minimum: 2048, maximum: 65565, ErrorMessage = "容許範圍 2048 ~ 65535")]
    [Required(ErrorMessage = "為必要項目")]
    public required int Port { get; init; }
}

internal static partial class ProgramLoggerExtensions
{
    [LoggerMessage("Login: {Result}")]
    public static partial void LogLogin(this ILogger logger, LogLevel logLevel, bool result);
    [LoggerMessage("Echo")]
    public static partial void LogEcho(this ILogger logger, LogLevel logLevel);
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
