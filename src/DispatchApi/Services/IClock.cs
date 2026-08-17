namespace DispatchApi.Services;

/// <summary>
/// Time is injected so that assignment-timing logic can be unit tested
/// deterministically rather than by sleeping.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
