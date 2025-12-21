
using ProtocolFramework.Core;
using ProtocolFramework.Host;
using WebGame.Core.Protocols;

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Logging.ClearProviders()
    .AddSimpleConsole();

appBuilder.Services.AddOpenApi();

appBuilder.AddAspNetCoreProtocolHost();

var app = appBuilder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", (HttpContext httpContext) => TypedResults.Ok(new
{
    httpContext.Request.Method,
    httpContext.Request.Path,
    httpContext.Request.Query,
    httpContext.Request.Headers,
    httpContext.Request.Protocol,
}));

app.MapProtocol(async (IProtocolSession session, LoginRequest request, ILogger<Program> logger) =>
{
    logger.LogLogin(LogLevel.Information, request);
    await Task.Yield();
    var response = new LoginResponse(request.Account == "overing" && request.Password == "abc123");
    await session.SendAsync(response);
});

app.MapProtocol(async (IProtocolSession session, EchoRequest request, ILogger<Program> logger) =>
{
    logger.LogEcho(LogLevel.Information);
    await session.SendAsync(new EchoResponse());
});

await app.RunAsync();

internal static partial class ProgramLoggerExtensions
{
    [LoggerMessage("Login: {Request}")]
    public static partial void LogLogin(this ILogger logger, LogLevel logLevel, LoginRequest request);
    [LoggerMessage("Echo")]
    public static partial void LogEcho(this ILogger logger, LogLevel logLevel);
}
