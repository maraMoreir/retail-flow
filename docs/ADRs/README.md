# Architecture Decision Records

One file per significant, hard-to-reverse decision — not a design doc, a record of
*why* something is the way it is, so nobody has to reconstruct the reasoning (or
re-litigate it) from Slack history later.

| # | Title | Status |
|---|-------|--------|
| [002](002-outbox-pattern-for-reliability.md) | Outbox pattern for reliable event delivery | Accepted |
| [006](006-messaging-and-mediator-library-choice.md) | Messaging & mediator library choice (Wolverine) | Accepted |
| 001 | Saga pattern for distributed transactions | Not yet written — no multi-step saga exists yet to design one against |
| 003 | Event sourcing consideration | Not yet written |
| 004 | CQRS eventual consistency | Not yet written — Reporting has no projections yet |
| 005 | Security strategy | Not yet written — see docs/ARCHITECTURE.md §Security for the interim summary |

Numbering is stable once assigned (matches the original project plan) but not
necessarily sequential in *arrival* order — 002 and 006 exist because Phase 1
actually needed them; 001/003/004/005 will land as the code that motivates each
decision gets built.

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
