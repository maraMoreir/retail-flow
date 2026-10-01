# ADR-006: Messaging & Mediator Library Choice

## Status
Accepted

## Context
The original project plan specified MediatR for in-process CQRS command/query
dispatch and MassTransit for the RabbitMQ transport and Saga orchestration - both
reasonable, extremely common choices at the time the plan was written. While
setting up Phase 1 (2026-08-22), checking current NuGet metadata for both showed:

- **MediatR** (currently 14.x): requires a paid license key for production use as
  of its commercial licensing change (`MEDIATR_LICENSE_KEY`, registration at
  MediatR.io) - confirmed via the package's NuGet listing.
- **MassTransit** (currently 9.x): commercial-only, per its own NuGet listing:
  *"MassTransit is a commercial product that must be licensed."* Only versions up
  to 8.x remain under the original permissive open-source license, and 8.x
  receives no further updates now that the project has moved on.

Since RetailFlow is meant as a lasting reference implementation (and the explicit
requirement for this pass was "everything free"), building the core messaging
story on two libraries that have since gone commercial - one of them EOL on its
last free version - was not an acceptable foundation, regardless of how closely
the original code samples matched their APIs.

## Decision
Use **Wolverine** (MIT license, `WolverineFx.*` packages, actively maintained)
for all of it: in-process command/query dispatch, the RabbitMQ transport, Saga
orchestration (when needed), and the transactional outbox/inbox (see
[ADR-002](002-outbox-pattern-for-reliability.md)).

The practical difference from the plan's original MediatR-shaped code samples:
Wolverine has **no `IRequestHandler<TRequest, TResponse>` interface to
implement**. A handler is a plain class Wolverine discovers by naming
convention - a class ending in `Handler` with a public `Handle`/`HandleAsync`
method:

```csharp
public static class CreateSaleHandler
{
    public static SaleDto Handle(CreateSaleCommand command, RetailFlowDbContext db)
    {
        // ...
    }
}
```

`RetailFlow.Application` therefore has **zero package dependency on Wolverine** -
see the `Application_ShouldNotDependDirectlyOnWolverine` architecture test in
[`RetailFlow.ArchitectureTests`](https://github.com/maraMoreir/retail-flow/blob/dev/tests/RetailFlow.ArchitectureTests/LayerDependencyTests.cs).
Only `RetailFlow.Infrastructure` (the composition-root wiring, in
[`DependencyInjection.cs`](https://github.com/maraMoreir/retail-flow/blob/dev/src/RetailFlow.Infrastructure/DependencyInjection.cs))
references it, which is a *stronger* separation of concerns than the original
MediatR-based design, where every handler had to implement a MediatR interface
and every Application project had to reference the MediatR package directly.

The lightweight `ICommand`/`ICommand<TResponse>`/`IQuery<TResponse>` marker
interfaces kept in `RetailFlow.Application/Common/Messages.cs` are purely
documentation/discoverability - Wolverine ignores them entirely.

## Consequences
- **Positive**: zero licensing risk, actively maintained, one dependency instead
  of two.
- **Positive**: Application layer is framework-agnostic for messaging, not just
  for persistence - a bigger deviation from (and improvement over) the original
  plan's Clean Architecture story than initially scoped.
- **Positive**: transactional outbox/inbox and EF Core integration come from the
  same vendor/package family, rather than gluing MediatR + MassTransit + a
  hand-rolled outbox together.
- **Negative**: convention-based handler discovery is less explicit at the call
  site than an interface you can "go to definition" on - mitigated by the marker
  interfaces above and by keeping handler classes co-located and consistently
  named.
- **Negative**: smaller community/ecosystem than MediatR+MassTransit had at their
  peak, though Wolverine is the actively-developed successor built by much of the
  same team behind the earlier Jasper project and MassTransit's own alumni.
- **Neutral**: dev-time runtime code generation needs the
  `WolverineFx.RuntimeCompilation` package (core Wolverine no longer bundles a
  Roslyn compiler); production deployments should switch to pre-generated static
  code (`dotnet run -- codegen write` + `TypeLoadMode.Static`) to drop that
  dependency and the associated startup cost - not yet done, tracked as a Phase 9
  follow-up.

## Alternatives Considered
- **MediatR + MassTransit** (the original plan): rejected - both commercial now,
  and MassTransit's last free version (8.x) is a dead end.
- **Custom mediator + `MassTransit` pinned to 8.x**: stays closest to the plan's
  original code samples, but ships something explicitly unmaintained on day one -
  a bad foundation for a reference project meant to last.
- **Custom mediator + `DotNetCore.CAP`** (MIT, purpose-built for the
  outbox+RabbitMQ+EF Core combination): a reasonable middle ground that would have
  kept a hand-rolled, DB-visible `OutboxMessage` table, but Saga orchestration
  would still need to be built by hand on top of it. Wolverine covers the same
  ground plus Saga support in one package.
- **Fully hand-rolled** (custom mediator + raw `RabbitMQ.Client` + hand-written
  outbox poller + hand-written saga orchestrator): maximum control and zero
  dependencies, but reimplements at-least-once delivery, idempotency, and
  poison-message handling from scratch — real distributed-systems correctness
  work that a mature library already gets right. Not worth it for this project's
  goals.
