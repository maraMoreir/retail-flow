using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;

namespace RetailFlow.IntegrationTests;

/// <summary>
/// Boots the real RetailFlow.Api host - Wolverine, EF Core, the Postgres-backed
/// transactional outbox/inbox, JWT auth, health checks, all of it - against real,
/// disposable Postgres/RabbitMQ/Redis containers. No mocks: this is what actually
/// proves the RetailFlow.Infrastructure wiring works end to end, not just that it
/// compiles.
/// </summary>
public sealed class HealthCheckTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management-alpine").Build();

    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(
            _postgres.StartAsync(),
            _rabbitMq.StartAsync(),
            _redis.StartAsync());

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", _postgres.GetConnectionString());
            builder.UseSetting("RabbitMq:Uri", _rabbitMq.GetConnectionString());
            builder.UseSetting("Redis:ConnectionString", _redis.GetConnectionString());
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(),
            _rabbitMq.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Ready_ReturnsHealthy_WhenEveryDependencyIsReachable()
    {
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"status\": \"Healthy\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Live_ReturnsHealthy_WithoutCheckingAnyDependency()
    {
        // Even with real containers up, /health/live should report healthy by
        // definition - it deliberately runs zero checks (Predicate = _ => false).
        using var client = _factory!.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
