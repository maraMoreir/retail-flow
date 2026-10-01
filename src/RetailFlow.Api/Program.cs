using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using RetailFlow.Api.Authentication;
using RetailFlow.Api.ErrorHandling;
using RetailFlow.Api.HealthChecks;
using RetailFlow.Api.Middleware;
using RetailFlow.Application;
using RetailFlow.Infrastructure;
using RetailFlow.Reporting;
using Serilog;

// Two-stage Serilog init: a minimal bootstrap logger captures anything that goes
// wrong before configuration/DI are even up, then gets replaced by the fully
// configured one below.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Application", "RetailFlow.Api")
        .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
        .WriteTo.Console()
        .WriteTo.Seq(context.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

    builder.AddRetailFlowInfrastructure();
    builder.Services.AddApplication();
    builder.Services.AddReporting(builder.Configuration);

    builder.Services.AddOpenApi();

    var keycloakAuthority = builder.Configuration["Keycloak:Authority"]
        ?? throw new InvalidOperationException("Missing required configuration: Keycloak:Authority.");
    var keycloakAudience = builder.Configuration["Keycloak:Audience"]
        ?? throw new InvalidOperationException("Missing required configuration: Keycloak:Audience.");

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = keycloakAuthority;
            options.Audience = keycloakAudience;
            // Keycloak in dev mode serves its realm over plain HTTP - fine locally,
            // never in a real environment (see docs/SECURITY.md once written).
            options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });

    builder.Services.AddTransient<IClaimsTransformation, KeycloakRoleClaimsTransformation>();

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("Manager", policy => policy.RequireRole("manager", "admin"))
        .AddPolicy("Customer", policy => policy.RequireRole("customer"))
        .AddPolicy("AdminOnly", policy => policy.RequireRole("admin"));

    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
    builder.Services.AddProblemDetails();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseExceptionHandler();
    app.UseMiddleware<CorrelationIdMiddleware>();
    app.UseSerilogRequestLogging();

    app.UseHttpsRedirection();
    app.UseAuthentication();
    app.UseAuthorization();

    // /health/live: is the process itself up - no dependency checks, always cheap.
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false,
        ResponseWriter = HealthCheckResponseWriter.WriteAsync,
    });

    // /health/ready: can this instance actually serve traffic right now.
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = HealthCheckResponseWriter.WriteAsync,
    });

    // /health: everything registered, for local/manual debugging.
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = HealthCheckResponseWriter.WriteAsync,
    });

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    // HostAbortedException is thrown by `dotnet ef` design-time tooling (and by
    // WebApplicationFactory in tests) spinning the host up just far enough to
    // inspect it - not a real startup failure, so it's excluded here and left to
    // propagate normally. Everything else: log with full detail, THEN rethrow -
    // swallowing it would make the process exit 0 on a failed startup, which
    // breaks orchestrator health/restart logic and silently leaves
    // WebApplicationFactory with no host to test against.
    Log.Fatal(ex, "RetailFlow.Api terminated unexpectedly during startup");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

// Top-level statements generate an `internal` Program class by default; this partial
// declaration makes it public so RetailFlow.IntegrationTests can boot the whole app
// via WebApplicationFactory<Program>.
public partial class Program;
