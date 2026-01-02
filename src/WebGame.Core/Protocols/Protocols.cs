
namespace WebGame.Core.Protocols;

public sealed record class LoginRequest(string Account, string Password);

public sealed record class LoginResponse(string AccessToken, string RefreshToken);

public sealed record class RefreshRequest(string AccessToken, string RefreshToken);

public sealed record class RefreshResponse(string AccessToken, string RefreshToken);

public sealed record class EchoRequest();

public sealed record class EchoResponse();
