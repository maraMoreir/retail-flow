namespace RetailFlow.Infrastructure.Caching;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>e.g. localhost:6379</summary>
    public required string ConnectionString { get; init; }
}
