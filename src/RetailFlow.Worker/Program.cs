using RetailFlow.Infrastructure;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, configuration) => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Application", "RetailFlow.Worker")
        .Enrich.WithProperty("Environment", builder.Environment.EnvironmentName)
        .WriteTo.Console()
        .WriteTo.Seq(builder.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

    // Same infrastructure wiring as RetailFlow.Api: Postgres, the Wolverine bus
    // (RabbitMQ transport + transactional outbox/inbox), Redis, health checks.
    // Wolverine registers its own IHostedService to listen on the configured
    // RabbitMQ queues, which is what actually keeps this process alive and busy -
    // there is deliberately no hand-rolled BackgroundService here. Consumers for
    // Inventory/Fiscal/Notification/Reporting land in RetailFlow.Application as
    // those bounded contexts are implemented; Wolverine picks them up automatically
    // via the assembly scan configured in RetailFlow.Infrastructure.
    builder.AddRetailFlowInfrastructure();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "RetailFlow.Worker terminated unexpectedly during startup");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
