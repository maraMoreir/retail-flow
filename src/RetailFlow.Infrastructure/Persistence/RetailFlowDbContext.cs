using Microsoft.EntityFrameworkCore;

namespace RetailFlow.Infrastructure.Persistence;

/// <summary>
/// The write-side DbContext shared by every bounded context. Each context adds its
/// own DbSet&lt;T&gt; and an IEntityTypeConfiguration&lt;T&gt; (picked up automatically
/// below) as it's implemented - Phase 1 only wires the plumbing, so there are no
/// entities registered yet.
///
/// Wolverine owns its own durable-messaging tables (outbox/inbox) in a separate
/// "wolverine" schema, created at startup by WolverineFx.Postgresql - they are NOT
/// part of this DbContext's model. See docs/ADRs/002-outbox-pattern-for-reliability.md.
/// </summary>
public sealed class RetailFlowDbContext(DbContextOptions<RetailFlowDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RetailFlowDbContext).Assembly);
    }
}
