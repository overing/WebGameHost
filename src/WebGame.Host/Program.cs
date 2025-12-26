
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using ProtocolFramework.Core;
using ProtocolFramework.Core.Serialization;
using ProtocolFramework.Host;
using WebGame.Core.Protocols;

Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Logging.ClearProviders()
    .AddSimpleConsole();

appBuilder.Services.AddOpenApi();

appBuilder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = "your-issuer",
            ValidAudience = "your-audience",
            IssuerSigningKeys = [new SymmetricSecurityKey(Encoding.UTF8.GetBytes("your-secret-key-at-least-32-chars"))],
            // IssuerSigningKeyResolver = (token, securityToken, kid, parameters) =>
            // {
            //     return [parameters.IssuerSigningKey];
            // }
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;

                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase))
                {
                    // logger.LogInformation($"Path: {path}, Set token: {accessToken}");
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            },

            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogAuthenticationFailed(LogLevel.Error, context.Exception);
                return Task.CompletedTask;
            },

            OnTokenValidated = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogTokenValidated(LogLevel.Information, context.Principal?.Identity?.Name!);
                return Task.CompletedTask;
            }
        };
    });

appBuilder.AddAspNetCoreProtocolHost(options => options.RegisterAssembly(typeof(EchoRequest).Assembly));
appBuilder.Services.AddSingleton<JwtService>();

var app = appBuilder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseWebSockets();

app.MapGet("/", (HttpContext httpContext) => TypedResults.Ok(new
{
    httpContext.Request.Method,
    httpContext.Request.Path,
    httpContext.Request.Query,
    httpContext.Request.Headers,
    httpContext.Request.Protocol,
}));

app.MapPost("/api/login", async ([FromBody] LoginRequest request, [FromServices] JwtService jwt) =>
{
    if (request.Account == "player1" && request.Password == "password")
    {
        var token = jwt.GenerateToken(request.Account);
        return Results.Ok(new { token });
    }
    return Results.Unauthorized();
});

app.Map("/ws", async (HttpContext httpContext, [FromServices] IWebSocketConnectionManager manager) =>
{
    if (!httpContext.User.Identity?.IsAuthenticated ?? true)
    {
        httpContext.Response.StatusCode = 401;
        return;
    }

    if (!httpContext.WebSockets.IsWebSocketRequest)
    {
        httpContext.Response.StatusCode = 400;
        return;
    }

    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
    var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
    await manager.HandleConnectionAsync(httpContext, userId, webSocket);
});

app.MapProtocol(async (IProtocolSession session, EchoRequest request, ILogger<Program> logger) =>
{
    logger.LogEcho(LogLevel.Information);
    await session.SendAsync(new EchoResponse());
});

await app.RunAsync();

internal static partial class ProgramLoggerExtensions
{
    [LoggerMessage("Token 驗證成功: {Account}")]
    public static partial void LogTokenValidated(this ILogger logger, LogLevel logLevel, string account);

    [LoggerMessage("驗證失敗")]
    public static partial void LogAuthenticationFailed(this ILogger logger, LogLevel logLevel, Exception exception);

    [LoggerMessage("Echo")]
    public static partial void LogEcho(this ILogger logger, LogLevel logLevel);
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class JwtService
{
    private readonly string _key = "your-secret-key-at-least-32-chars";

    public string GenerateToken(string userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Name, userId)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "your-issuer",
            audience: "your-audience",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
