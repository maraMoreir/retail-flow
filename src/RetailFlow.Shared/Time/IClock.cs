namespace RetailFlow.Shared.Time;

/// <summary>
/// Testable substitute for <see cref="DateTimeOffset.UtcNow"/>. Inject this instead
/// of calling UtcNow directly so tests can control "now".
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
