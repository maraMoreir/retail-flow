# ADR-001: Saga Pattern for Distributed Transactions

## Status
Accepted (design only — see [Implementation status](#implementation-status) below; no code exists yet)

## Context
Completing a sale is one business operation that spans four bounded contexts:
Sales confirms payment, Inventory reserves stock, Fiscal authorizes a document,
Notification tells the customer. Each context is its own consistency boundary
with its own aggregate and its own database transaction — there is no single ACID
transaction that can span all four. If inventory reservation fails after payment
was confirmed, or fiscal authorization fails after stock was reserved, the system
needs to know how to *undo* the steps that already succeeded, not just fail
silently or leave the sale stuck half-done.

## Decision
Orchestrate the create-sale flow as a Wolverine `Saga`, correlated on `SaleId`,
rather than pure choreography (each context reacting to the last one's event with
no central coordinator). Orchestration was chosen over choreography specifically
*because* failure needs an explicit, visible compensation path — with four steps
and two different failure points to compensate, "who's responsible for undoing
what" is much easier to audit in one saga class than reconstructed from event
handlers scattered across four projects.

```csharp
public class CreateSaleSaga : Saga
{
    public Guid SaleId { get; set; } // matches Wolverine's "{Type}Id" correlation
                                      // convention - see Handle() below
    public SaleSagaStatus Status { get; set; }

    public static CreateSaleSaga Start(SaleCompletedEvent completed) =>
        new()
        {
            SaleId = completed.SaleId,
            Status = SaleSagaStatus.AwaitingInventoryReservation,
        };

    // Wolverine routes InventoryReservedEvent/InventoryReservationFailedEvent
    // here because InventoryReservedEvent carries a SaleId property matching
    // this saga's correlation ID - no explicit registration needed.
    public FiscalAuthorizationRequested Handle(InventoryReservedEvent reserved)
    {
        Status = SaleSagaStatus.AwaitingFiscalAuthorization;
        return new FiscalAuthorizationRequested(SaleId);
    }

    public SaleCancelledEvent Handle(InventoryReservationFailedEvent failed)
    {
        MarkCompleted();
        return new SaleCancelledEvent(SaleId, "Inventory reservation failed: " + failed.Reason);
    }

    public NotificationRequested Handle(FiscalGeneratedEvent generated)
    {
        Status = SaleSagaStatus.AwaitingNotification;
        return new NotificationRequested(SaleId);
    }

    // Compensating action: fiscal failed AFTER inventory was already reserved -
    // give the stock back before cancelling the sale.
    public (InventoryReleaseRequested, SaleCancelledEvent) Handle(FiscalGenerationFailedEvent failed)
    {
        MarkCompleted();
        return (
            new InventoryReleaseRequested(SaleId, "Fiscal authorization failed"),
            new SaleCancelledEvent(SaleId, "Fiscal authorization failed: " + failed.Reason));
    }

    public void Handle(NotificationSentEvent sent) => MarkCompleted();
}
```

Persistence reuses the exact EF Core + Postgres wiring already in place for the
outbox (see [ADR-002](002-outbox-pattern-for-reliability.md)) —
`WolverineFx.EntityFrameworkCore` persists saga state through
`RetailFlowDbContext` with no separate storage mechanism (e.g. Marten) needed.

## Consequences
- **Positive**: one place to read the entire happy path AND every compensation —
  a new engineer (or ADR reader) doesn't have to trace event handlers across four
  projects to understand "what happens if fiscal authorization fails."
- **Positive**: saga state (`SaleSagaStatus`) gives an operator a queryable answer
  to "where is sale X stuck" without grepping logs across services.
- **Negative**: the saga becomes a coordination point every context's events flow
  through — a bug in the saga itself can stall sales that would otherwise have
  completed fine. Mitigated by keeping the saga's own logic trivial (route event →
  send next command / compensate) and pushing all actual business logic into each
  context's own handlers.
- **Negative**: compensating actions are "best effort," not a true rollback —
  `InventoryReleaseRequested` after a fiscal failure assumes the release itself
  succeeds. A release that fails needs its own retry/alerting story (Wolverine's
  message-retry policy in `RetailFlow.Infrastructure/DependencyInjection.cs`
  covers transient failures; a release that fails *every* retry still needs a
  human, same as any distributed system).

## Alternatives Considered
- **Pure choreography** (every context listens for the previous context's event
  and reacts, no central saga): rejected — compensation logic would be spread
  across Inventory's, Fiscal's, and Notification's own event handlers with no
  single place to see the whole flow or its failure paths.
- **Two-phase commit / distributed transaction coordinator**: rejected for the
  same reason as in [ADR-002](002-outbox-pattern-for-reliability.md) — not
  practical across Postgres + RabbitMQ, and saga compensation is the standard
  answer to this class of problem in event-driven systems.

## Implementation status
Nothing above is wired into the codebase yet — `SaleCompletedEvent`,
`InventoryReservedEvent`, `FiscalGeneratedEvent`, and `NotificationSentEvent` exist
as domain event contracts (`RetailFlow.Domain/{Sales,Inventory,Fiscal,Notification}/Events/`),
but the commands the saga would send (`FiscalAuthorizationRequested`,
`NotificationRequested`, `InventoryReleaseRequested`, ...), their handlers, and
the `CreateSaleSaga` class itself do not exist — there's no `Sale` aggregate yet
to raise `SaleCompletedEvent` in the first place. This ADR records the *design*
per the project's Phase 2 scope; the saga gets built in Phase 3 alongside the
aggregates it coordinates.
