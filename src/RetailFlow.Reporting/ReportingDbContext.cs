using Microsoft.EntityFrameworkCore;

namespace RetailFlow.Reporting;

/// <summary>
/// Read-only projection store for dashboards/KPIs (CQRS read side). Populated
/// asynchronously by RetailFlow.Worker as it reacts to domain events published by
/// the write side - RetailFlow.Api only ever queries this, never writes to it.
/// No DbSets yet; Phase 1 wires the connection, projections land as bounded
/// contexts are implemented.
/// </summary>
public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReportingDbContext).Assembly);
    }
}
