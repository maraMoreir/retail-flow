# ADR-002: Outbox Pattern for Reliable Event Delivery

## Status
Accepted

## Context
Creating a sale (and every other write in RetailFlow) does two things that must
either both happen or neither happen: persist the aggregate's new state, and tell
the rest of the system about it (`SaleCompletedEvent` → reserve inventory, generate
a fiscal document, notify the customer, update the read model). Postgres and
RabbitMQ are two separate systems with no shared transaction. Writing to the
database and then publishing to RabbitMQ as two independent steps has a failure
window in between: if the process crashes, gets OOM-killed, or the network drops
after the commit but before the publish, the event is lost forever and every
downstream consumer silently never finds out the sale happened. Publishing first
and writing to the database second has the opposite problem: a downstream
consumer can react to a sale that then fails to persist.

## Decision
Use the transactional outbox pattern: write the "this needs to be sent" record to
Postgres in the *same* database transaction as the aggregate itself, and only
publish to RabbitMQ afterward, from that durably-stored record — never directly
from the request handler.

Rather than hand-rolling an `OutboxMessage` table, an `OutboxService`, and a
polling `BackgroundService` (as originally sketched in the project plan), this is
implemented with Wolverine's built-in Postgres-backed durable messaging
(`WolverineFx.Postgresql` + `WolverineFx.EntityFrameworkCore`), configured once in
[`RetailFlow.Infrastructure/DependencyInjection.cs`](https://github.com/maraMoreir/retail-flow/blob/dev/src/RetailFlow.Infrastructure/DependencyInjection.cs):

```csharp
opts.PersistMessagesWithPostgresql(postgresConnectionString);
opts.Services.AddDbContextWithWolverineIntegration<RetailFlowDbContext>(
    db => db.UseNpgsql(postgresConnectionString));
opts.UseEntityFrameworkCoreTransactions();
opts.Policies.AutoApplyTransactions();
```

`UseEntityFrameworkCoreTransactions()` + `AutoApplyTransactions()` means: for any
Wolverine handler that also touches `RetailFlowDbContext`, Wolverine wraps
`DbContext.SaveChangesAsync()` and its own outgoing-message persistence in one
transaction automatically. A handler just calls `context.Sales.Add(sale)` and
returns the event to publish (or raises it) — the outbox mechanics are invisible
at the call site. Wolverine provisions its own tables for this (a `wolverine`
schema, separate from `RetailFlowDbContext`'s model) and runs a background
dispatcher that relays anything not yet confirmed-delivered to RabbitMQ, retrying
on failure — this is the same mechanism as the hand-rolled `OutboxPoller` in the
original plan, just implemented once, tested, and maintained upstream instead of
by us.

See also: [ADR-006](006-messaging-and-mediator-library-choice.md) for why
Wolverine specifically.

## Consequences
- **Positive**: no lost events on crash/restart; no dual-write inconsistency;
  delivery guarantee (at-least-once) without hand-writing the tricky part
  (transaction coordination, retry/backoff, poison-message handling).
- **Positive**: consumers must be written idempotently regardless (at-least-once
  delivery, not exactly-once) — this was already going to be true with a
  hand-rolled outbox, so it's not a new cost.
- **Negative**: an extra schema (`wolverine`) inside the same Postgres database to
  understand when debugging - "why hasn't this event arrived yet" now means
  looking at Wolverine's envelope tables, not a custom `OutboxMessages` table with
  application-defined column names.
- **Negative**: ties the outbox implementation to Wolverine's storage schema and
  release cadence rather than fully owning it - acceptable given Wolverine is
  MIT-licensed and the schema is Postgres tables we can inspect/query directly if
  ever needed.

## Alternatives Considered
- **Hand-rolled `OutboxMessage` table + polling `BackgroundService`** (the
  original plan): more code to write, test, and maintain for a well-understood
  problem that a maintained library already solves correctly - rejected as
  wasted effort for a "reference implementation other teams learn from," not
  because it's a bad pattern to know how to build.
- **Publish directly, no outbox**: rejected outright — this is exactly the
  dual-write problem described above.
- **Two-phase commit (XA) between Postgres and RabbitMQ**: RabbitMQ's XA support
  is limited and reduces broker availability/throughput; not worth it when the
  outbox pattern gets the same durability guarantee without a distributed
  transaction coordinator.
