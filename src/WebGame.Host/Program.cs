
using ProtocolFramework.Core;
using ProtocolFramework.Host;
using WebGame.Core.Protocols;

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Logging.ClearProviders()
    .AddSimpleConsole();

appBuilder.Services.AddOpenApi();

appBuilder.AddProtocolFramework();

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

app.MapProtocol(async (IProtocolSession session, LoginRequest request) =>
{
    await Task.Yield();
    var response = new LoginResponse(request.Account == "overing" && request.Password == "abc123");
    await session.SendAsync(response);
});

app.MapProtocol(async (IProtocolSession session, EchoRequest request) =>
{
    await session.SendAsync(new EchoResponse());
});

await app.RunAsync();
