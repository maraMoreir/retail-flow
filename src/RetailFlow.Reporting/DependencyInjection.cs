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

        services.AddDbContext<ReportingDbContext>(db => db.UseNpgsql(connectionString));

        return services;
    }
}
