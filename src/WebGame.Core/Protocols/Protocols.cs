
namespace WebGame.Core.Protocols;

public sealed record class LoginRequest(string Account, string Password);

public sealed record class EchoRequest();

public sealed record class EchoResponse();
