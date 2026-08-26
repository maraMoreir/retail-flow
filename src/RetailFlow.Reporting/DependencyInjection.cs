using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace RetailFlow.Reporting;

public static class DependencyInjection
{
    public static IServiceCollection AddReporting(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Missing required configuration: ConnectionStrings:Postgres.");

        // "Database: retry 3x, exponential backoff" resilience policy from the
        // project plan - a transient Postgres blip (failover, brief network hiccup)
        // gets retried instead of surfacing as a 500 to whoever's reading a report.
        services.AddDbContext<ReportingDbContext>(db => db.UseNpgsql(
            connectionString,
            npgsql => npgsql.EnableRetryOnFailure(
                maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)));

        return services;
    }
}
