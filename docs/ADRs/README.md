# Architecture Decision Records

One file per significant, hard-to-reverse decision — not a design doc, a record of
*why* something is the way it is, so nobody has to reconstruct the reasoning (or
re-litigate it) from Slack history later.

| # | Title | Status |
|---|-------|--------|
| [001](001-saga-pattern-for-transactions.md) | Saga pattern for distributed transactions | Accepted (design only — no `Sale` aggregate exists yet to trigger it) |
| [002](002-outbox-pattern-for-reliability.md) | Outbox pattern for reliable event delivery | Accepted, implemented |
| [004](004-cqrs-eventual-consistency.md) | CQRS eventual consistency | Accepted (mechanism implemented; no projection exists yet to measure against) |
| [006](006-messaging-and-mediator-library-choice.md) | Messaging & mediator library choice (Wolverine) | Accepted, implemented |
| 003 | Event sourcing consideration | Not yet written — not needed unless/until a concrete case for it shows up |
| 005 | Security strategy | Not yet written — see docs/ARCHITECTURE.md §Security for the interim summary |

Numbering is stable once assigned (matches the original project plan) but not
necessarily sequential in *arrival* order — 002 and 006 landed in Phase 1
because the outbox/messaging plumbing was needed immediately; 001 and 004 landed
in Phase 2 as domain-design decisions; 003/005 will land once the code that
motivates each decision gets built.

## Template

```markdown
# ADR-NNN: Title

## Status
Proposed | Accepted | Superseded by ADR-XXX

## Context
What problem forced this decision? What constraints applied?

## Decision
What was decided, stated plainly.

## Consequences
What gets easier, what gets harder, what risk is being accepted.

## Alternatives Considered
What else was on the table and why it lost.
```
