using JasperFx.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RabbitMQ.Client;
using RetailFlow.Infrastructure.Caching;
using RetailFlow.Infrastructure.Messaging;
using RetailFlow.Infrastructure.Persistence;
using RetailFlow.Shared.Correlation;
using RetailFlow.Shared.Time;
using StackExchange.Redis;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.FluentValidation;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;

namespace RetailFlow.Infrastructure;

/// <summary>
/// Everything infrastructure-related that RetailFlow.Api and RetailFlow.Worker both
/// need is wired here, once, so the two hosts can never drift out of sync: EF Core +
/// Postgres, the Wolverine message bus (RabbitMQ transport, Postgres-backed
/// transactional outbox/inbox, FluentValidation middleware, handler discovery from
/// RetailFlow.Application), Redis, and health checks.
/// </summary>
public static class DependencyInjection
{
    public static IHostApplicationBuilder AddRetailFlowInfrastructure(this IHostApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var services = builder.Services;

        var postgresConnectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Missing required configuration: ConnectionStrings:Postgres.");

        var rabbitMq = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>()
            ?? throw new InvalidOperationException($"Missing required configuration section: {RabbitMqOptions.SectionName}.");

        var redis = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>()
            ?? throw new InvalidOperationException($"Missing required configuration section: {RedisOptions.SectionName}.");

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ICorrelationIdProvider, AmbientCorrelationIdProvider>();

        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(redis.ConnectionString));

        builder.UseWolverine(opts =>
        {
            // Handlers live in RetailFlow.Application, not in this (Infrastructure)
            // or the host assembly, so Wolverine has to be told to scan it explicitly.
            opts.Discovery.IncludeAssembly(Application.DependencyInjection.ApplicationAssembly);

            opts.UseRabbitMq(new Uri(rabbitMq.Uri)).AutoProvision();

            // Transactional outbox/inbox: Wolverine persists not-yet-sent messages in
            // Postgres in the SAME transaction as the EF Core SaveChanges call below,
            // and only actually publishes to RabbitMQ once that transaction commits.
            // See docs/ADRs/002-outbox-pattern-for-reliability.md.
            opts.PersistMessagesWithPostgresql(postgresConnectionString);

            // "Database: retry 3x, exponential backoff" resilience policy from the
            // project plan - applies to every EF Core call against this DbContext,
            // not just Wolverine's own transactional-outbox writes.
            opts.Services.AddDbContextWithWolverineIntegration<RetailFlowDbContext>(
                db => db.UseNpgsql(postgresConnectionString, npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)));

            opts.UseEntityFrameworkCoreTransactions();
            opts.Policies.AutoApplyTransactions();

            opts.UseFluentValidation();

            // Message-queue resilience: a transient failure in a handler (e.g. a
            // brief Postgres blip) gets 5 attempts with growing backoff before
            // Wolverine gives up and moves the message to its error queue - the
            // "Message queue: 5 retries, exponential backoff" policy from the
            // project plan. Sagas/handlers that need different behavior for a
            // specific exception type can add a more specific OnException<T>()
            // policy - the most specific matching policy wins.
            opts.OnException<Exception>()
                .RetryWithCooldown(
                    100.Milliseconds(),
                    250.Milliseconds(),
                    500.Milliseconds(),
                    1.Seconds(),
                    2.Seconds());

            // WolverineFx.RuntimeCompilation (referenced above) auto-registers here
            // and compiles handler-dispatch code with Roslyn on first use - fine for
            // dev/CI. Once there's a real deployment pipeline, switch to pre-generated
            // static code (`dotnet run -- codegen write` + TypeLoadMode.Static) to
            // drop the Roslyn dependency and startup cost from production; see
            // https://wolverinefx.net/guide/codegen.html.
        });

        // Tagged "ready" so RetailFlow.Api's /health/ready endpoint can require all
        // of them while /health/live stays a cheap "is the process up" check.
        services.AddHealthChecks()
            .AddNpgSql(postgresConnectionString, name: "postgresql", tags: ["ready"])
            .AddRabbitMQ(CreateRabbitMqConnectionAsync(rabbitMq.Uri), name: "rabbitmq", tags: ["ready"])
            .AddRedis(redis.ConnectionString, name: "redis", tags: ["ready"]);

        return builder;
    }

    /// <summary>
    /// A dedicated, lazily-created connection for the health check only - kept
    /// separate from whatever connection Wolverine's RabbitMQ transport manages
    /// internally for actual message traffic.
    /// </summary>
    private static Func<IServiceProvider, Task<IConnection>> CreateRabbitMqConnectionAsync(string uri)
    {
        var lazyConnection = new Lazy<Task<IConnection>>(
            () => new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync());

        return _ => lazyConnection.Value;
    }
}
