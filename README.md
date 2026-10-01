# RetailFlow

> An offline-first **Point of Sale** for Brazilian retail chains, designed to integrate with
> whatever ERP the customer already runs.

![Status](https://img.shields.io/badge/status-IN%20DEVELOPMENT-blue)
![.NET](https://img.shields.io/badge/.NET-10-blueviolet)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-17-blue)
![RabbitMQ](https://img.shields.io/badge/RabbitMQ-quorum_queues-orange)
![License](https://img.shields.io/badge/License-MIT-green)

---

## What it is

A POS for physical retail. It owns **what happens at the counter**:

| RetailFlow owns | The customer's ERP owns |
|---|---|
| Sale | Product catalog |
| Fiscal document (NFC-e / NF-e) | Price and commercial policy |
| Cash session | Stock balance |
| Payment / TEF capture | Customers |
| Return and exchange | Accounting and reporting |

RetailFlow **replicates** what the ERP owns so it can sell offline, and **reports back**
what the store did. It never registers a product, never sets a price, and is never the
source of truth for stock.

That boundary is the product.

## What makes it hard

Three things, and they are the whole reason the architecture looks the way it does.

**1. The store must keep selling when the link drops.** Not degrade — sell. That forces a
process running on hardware inside the store (**Store Edge**), a local fiscal number range,
and the A1 certificate on premises. It cannot be retrofitted.

**2. NFC-e must be authorized before the receipt prints**, because the receipt carries the
protocol. And SEFAZ goes down. So emission is **local**, with a short timeout and a fall
back to offline contingency (`tpEmis=9`) — a legal operating mode, not an error handler.

**3. It has to speak to any ERP.** SAP, Protheus, Sankhya — each with its own model,
vocabulary and transport (REST, SOAP, a file on SFTP). Without an anticorruption layer,
the first customer defines the product's domain and the second one requires a rewrite.

---

## Architecture

```
┌─ STORE (one per location) ──────────┐      ┌─ CLOUD ─────────────────────────┐
│                                     │      │                                 │
│   POS App ──► Store Edge ──► SQLite │      │   API Gateway                   │
│                   │                 │      │        │                        │
│                   ├──► Pinpad/TEF   │      │        ├──► Api                 │
│                   │                 │      │        │    Sales · Returns     │
│                   └──► SEFAZ        │      │        │    Cashier · Payments  │
│                        NFC-e signed │      │        │                        │
│                        locally      │      │        └──► Fiscal              │
│                                     │      │             NF-e · ranges       │
│   • catalog + price replica         │      │             reconciliation      │
│   • leased fiscal number range      │      │                                 │
│   • A1 certificate                  │      │   ErpConnector                  │
│   • local outbox                    │      │     canonical model + adapters  │
│   • cash session                    │      │        │                        │
│                                     │      │        └──► customer's ERP      │
└──────────────┬──────────────────────┘      └─────────────────────────────────┘
               │  HTTPS + Idempotency-Key
               └──────────────────────────────────────►
```

Full C4 model — Context, Container, Components, module dependency graph, device lifecycle
and personal-data flow — in [retail-flow.drawio](retail-flow.drawio) (9 pages).

Written overview: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

---

## Decisions

Decisions live in [docs/ADRs](docs/ADRs/README.md) — one file per significant,
hard-to-reverse choice, each recording the alternatives rejected and the consequences,
including the bad ones.

| ADR | Decision |
|---|---|
| [001](docs/ADRs/001-saga-pattern-for-transactions.md) | Saga pattern for distributed transactions |
| [002](docs/ADRs/002-outbox-pattern-for-reliability.md) | Outbox pattern for reliable event delivery |
| [004](docs/ADRs/004-cqrs-eventual-consistency.md) | CQRS eventual consistency |
| [006](docs/ADRs/006-messaging-and-mediator-library-choice.md) | Messaging & mediator library choice (Wolverine) |

---

## Stack

**Backend** — .NET 10 · ASP.NET Core · Wolverine (messaging + mediator) · EF Core
· PostgreSQL 17 · Redis · RabbitMQ · SQLite (Store Edge)

**Infrastructure** — Docker · Keycloak · GitHub Actions

**Observability** — OpenTelemetry over OTLP, one pipeline for logs, metrics and traces.

---

## Project structure

```
src/
  RetailFlow.Api              HTTP entry point, auth, health checks, error handling
  RetailFlow.Application      use cases, pipeline behaviours
  RetailFlow.Domain           Sales · Inventory · Fiscal · Notification
  RetailFlow.Infrastructure   persistence, messaging, external integrations
  RetailFlow.Reporting        read model
  RetailFlow.Worker           background consumers
  RetailFlow.Shared           value objects, Result, cross-cutting primitives
tests/
  RetailFlow.UnitTests
  RetailFlow.IntegrationTests
  RetailFlow.ArchitectureTests   enforces module boundaries at build time
  RetailFlow.ContractTests       one per ERP adapter
  RetailFlow.ChaosTests          offline, SEFAZ down, ERP down, compensation paths
  RetailFlow.PerformanceTests
docs/
  ARCHITECTURE.md
  ADRs/
```

---

## Branches

| Branch | Carries | How it receives changes |
|---|---|---|
| `main` | README, project structure and documentation | Documentation lands here directly |
| `staging` | Homologation | Pull request from `dev` |
| `dev` | Integration — all code | Feature branches |

Code never lands on `main` directly: it goes through a pull request from `dev` into
`staging` for homologation first.

---

## License

MIT
