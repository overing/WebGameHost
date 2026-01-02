
using System.Collections.Frozen;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ProtocolFramework.Core;
using ProtocolFramework.Host;
using WebGame.Core.Protocols;

var appBuilder = WebApplication.CreateBuilder(args);

appBuilder.Logging.ClearProviders()
    .AddSimpleConsole();

appBuilder.Services.AddOpenApi();

appBuilder.Services.AddMemoryCache();

appBuilder.AddAspNetCoreProtocolHost(options => options.RegisterAssemblyOf<EchoRequest>());
appBuilder.Services
    .AddSingleton<UserService>();
appBuilder.Services
    .AddSingleton<TokenService>()
    .AddOptions<TokenServiceOptions>()
    .BindConfiguration(nameof(TokenServiceOptions))
    .ValidateDataAnnotations()
    .ValidateOnStart();

appBuilder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, JwtBearerOptionsSetup>();

appBuilder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

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

app.MapPost("/api/login", ([FromBody] LoginRequest request, [FromServices] TokenService tokenService, [FromServices] UserService userService) =>
{
    if (userService.GetUser(request.Account, request.Password) is { } userId)
    {
        var accessToken = tokenService.GenerateAccessToken(userId);
        var refreshToken = tokenService.GenerateRefreshToken(userId);
        return Results.Ok(new LoginResponse(accessToken, refreshToken));
    }

    return Results.Unauthorized();
});

app.MapPost("/api/reflash", ([FromBody] RefreshRequest request, [FromServices] TokenService tokenService) =>
{
    if (tokenService.GetUserIdWithAccessToken(request.AccessToken) is { } userId &&
        tokenService.IsRefreshTokenAvailable(request.RefreshToken))
    {
        var accessToken = tokenService.GenerateAccessToken(userId);
        var refreshToken = tokenService.GenerateRefreshToken(userId);
        return Results.Ok(new RefreshResponse(accessToken, refreshToken));
    }

    return Results.Unauthorized();
});

app.Map("/ws", async (HttpContext httpContext, [FromServices] IWebSocketConnectionManager manager) =>
{
    if (!httpContext.User.Identity?.IsAuthenticated ?? true)
    {
        httpContext.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        return;
    }

    if (!httpContext.WebSockets.IsWebSocketRequest)
    {
        httpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
        return;
    }

    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value!;
    var webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
    await manager.HandleConnectionAsync(httpContext, userId, webSocket);
});

app.MapProtocol(async (IProtocolSession session, EchoRequest request, ILogger<Program> logger) =>
{
    // logger.LogEcho(LogLevel.Information);
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
internal sealed class UserService
{
    private readonly FrozenDictionary<(string Account, string Password), string> _users = new Dictionary<(string, string), string>
    {
        [("player1", "password")] = "10001", // TODO: 先暫時用 RAM 替代資料庫
    }.ToFrozenDictionary();

    public string? GetUser(string account, string password) => _users.TryGetValue((account, password), out var userId) ? userId : null;
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class TokenServiceOptions
{
    [Required]
    [MinLength(32)]
    public required string SecretKey { get; init; }

    [Required]
    public required string Issuer { get; init; }

    [Required]
    public required string Audience { get; init; }

    [Range(10, 60)]
    public int AccessTokenExpirationMinutes { get; init; }

    [Range(1, 14)]
    public int RefreshTokenExpirationDays { get; init; }
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class TokenService(IOptions<TokenServiceOptions> options, IMemoryCache cache)
{
    private readonly TokenServiceOptions _options = options.Value;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly IMemoryCache _cache = cache;

    public string? GetUserIdWithAccessToken(string accessToken)
    {
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _options.Issuer,
            ValidAudience = _options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey))
        };

        var result = _handler.ValidateTokenAsync(accessToken, validationParameters).GetAwaiter().GetResult();
        if (result.IsValid)
        {
            var jwtToken = result.SecurityToken as JsonWebToken;
            var userId = jwtToken?.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
            return userId;
        }

        return null;
    }

    public string GenerateAccessToken(string userId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey));
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Name, userId)
            ]),
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(_options.AccessTokenExpirationMinutes),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        return _handler.CreateToken(descriptor);
    }

    public bool IsRefreshTokenAvailable(string refreshToken) => _cache.TryGetValue(refreshToken, out _);

    public string GenerateRefreshToken(string userId)
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);
        var token = Convert.ToBase64String(randomBytes);
        _cache.Set(token, userId, TimeSpan.FromDays(_options.RefreshTokenExpirationDays));
        return token;
    }
}

[SuppressMessage("Performance", "CA1812", Justification = "This class is instantiated via DI")]
internal sealed class JwtBearerOptionsSetup(IOptions<TokenServiceOptions> jwtOptions) : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly TokenServiceOptions _jwtOptions = jwtOptions.Value;

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme) return;
        Configure(options);
    }

    public void Configure(JwtBearerOptions options)
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _jwtOptions.Issuer,
            ValidAudience = _jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SecretKey))
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
    }
}
