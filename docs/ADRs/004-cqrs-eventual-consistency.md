# ADR-004: CQRS with Eventual Consistency

## Status
Accepted

## Context
`RetailFlow.Reporting` (dashboards, KPIs) is a genuinely separate read model from
the write side — see [docs/ARCHITECTURE.md](../ARCHITECTURE.md): it has its own
`ReportingDbContext` and deliberately does not reference `RetailFlow.Domain`
(enforced by `RetailFlow.ArchitectureTests`). That separation only works if
something keeps the read model in sync with the write side, and that something
cannot be a synchronous call inside the write transaction — coupling `Sale`'s
transaction to also updating denormalized report tables would mean a slow/failing
report write could block or fail a sale, which is exactly backwards: reporting
should never be able to take checkout down.

## Decision
Reporting is updated asynchronously, after the fact: `RetailFlow.Worker` consumes
the same domain events the outbox already reliably delivers (`SaleCompletedEvent`,
`InventoryReservedEvent`, etc. — see
[ADR-002](002-outbox-pattern-for-reliability.md)) and projects them into
`ReportingDbContext`'s tables. Between a sale completing and a dashboard
reflecting it, there is a **target consistency window of 2-5 seconds** under
normal operation (outbox relay latency + RabbitMQ delivery + Worker processing —
all in the sub-second-to-low-single-digit-second range individually; 2-5s is the
end-to-end budget, not the latency of any one hop). This is a target, not yet an
enforced/measured SLO — no read-model projections exist yet to measure (see
[Implementation status](#implementation-status)).

Two things make the eventual-consistency window tolerable rather than confusing:

1. **Correlation IDs tie the whole chain together.** The correlation ID minted at
   the API edge (`CorrelationIdMiddleware`) flows through to every domain event
   and, once Wolverine's message headers carry it, into every log line the Worker
   produces while projecting that event — a request and its eventual read-model
   update can be traced end to end by one ID, even though they happen on two
   different hosts seconds apart. See
   [`RetailFlow.Shared/Correlation/ICorrelationIdProvider.cs`](https://github.com/maraMoreir/retail-flow/blob/dev/src/RetailFlow.Shared/Correlation/ICorrelationIdProvider.cs).
2. **At-least-once delivery means projections must be idempotent.** The outbox
   guarantees a domain event is delivered at least once, not exactly once —
   Worker projection handlers have to tolerate seeing `SaleCompletedEvent` for the
   same `SaleId` twice (e.g. upsert by `SaleId`, not blind insert) rather than
   assuming each event arrives exactly once.

## Consequences
- **Positive**: a slow or temporarily-down Reporting projection cannot fail or
  slow down a sale — the write side and read side genuinely cannot take each
  other down.
- **Positive**: `RetailFlow.Reporting` can be scaled, re-projected from scratch
  (replay every event from the beginning of the outbox), or even swapped for a
  different storage technology entirely without touching the write side at all.
- **Negative**: "read your own write" doesn't hold — a client that completes a
  sale and immediately queries the dashboard may not see it yet. Any UI built on
  top of Reporting needs to either tolerate that window or poll/subscribe rather
  than assume immediate consistency.
- **Negative**: projection handlers need explicit idempotency handling (upsert
  semantics) that a naive "just insert a row" implementation wouldn't need under
  an exactly-once assumption RetailFlow deliberately doesn't make.

## Alternatives Considered
- **Synchronous read-model update inside the write transaction**: rejected — see
  Context above; couples reporting availability/latency to checkout availability.
- **Change Data Capture (CDC) off the write-side tables** instead of consuming
  domain events: would remove the idempotency burden (a CDC stream naturally
  reflects committed state once) but ties the read model's shape to the write
  side's physical schema and loses the event's business meaning (a row changed,
  but why?) that `RetailFlow.Worker`'s handlers can act on. Revisit if
  event-driven projection turns out to be a maintenance burden in practice.
- **Synchronous read-through to the write side for "fresh" queries** (bypass
  Reporting for anything needing up-to-the-second data): not ruled out for a
  specific future query that genuinely needs it, but not the default — most
  dashboard/KPI use cases tolerate a few seconds of staleness in exchange for
  never touching the write side's load.

## Implementation status
The consistency mechanism (outbox → RabbitMQ → Worker) is built and verified
(`RetailFlow.IntegrationTests`), and correlation ID propagation into logs is live
today. What doesn't exist yet: any actual Reporting projection handler, because
no domain event has a real publisher yet (no `Sale` aggregate exists to raise
`SaleCompletedEvent` — see [ADR-001](001-saga-pattern-for-transactions.md)'s
implementation status for the same caveat). The 2-5s consistency window is
therefore a target carried over from the project plan, not yet something
measured against a real projection.
