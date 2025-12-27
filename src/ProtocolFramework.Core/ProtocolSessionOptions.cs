namespace ProtocolFramework.Core;

public sealed class ProtocolSessionOptions
{
    public int MaxQueueSize { get; init; } = 5000;
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
