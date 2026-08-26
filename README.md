# RetailFlow

> A staff-engineer-grade distributed retail platform built with .NET 10 — Clean
> Architecture, DDD, CQRS, the transactional outbox pattern, and full local
> observability, built as a reference implementation other teams can learn from.

![.NET](https://img.shields.io/badge/.NET-10-blueviolet)
![RabbitMQ](https://img.shields.io/badge/RabbitMQ-Message_Broker-orange)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-Database-blue)
![Redis](https://img.shields.io/badge/Redis-Cache-red)
![Keycloak](https://img.shields.io/badge/Keycloak-AuthN%2FAuthZ-blueviolet)
![Docker](https://img.shields.io/badge/Docker-Ready-2496ED)
![License](https://img.shields.io/badge/License-MIT-green)

---

## Overview

RetailFlow is an enterprise retail platform inspired by real-world Point of Sale
(POS) and ERP systems: sales, inventory, fiscal documents, notifications, and
reporting, coordinated through asynchronous messaging with a guaranteed-delivery
outbox instead of the request/response calls a simpler CRUD app would use.

The goal isn't a feature checklist — it's a reference for the operational
maturity (resilience, security, observability, testing depth) a platform team
would actually expect in production, documented well enough that someone else
could pick it up and keep building.

**Current status**: Phase 1 (Foundation) — see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#status-whats-real-vs-planned)
for exactly what's real vs. planned. Short version: the architecture, messaging,
security, and testing *scaffolding* all exist and are verified end-to-end; the
actual business logic (Sales, Inventory, Fiscal, Notification) hasn't been built
yet.

---

## Architecture at a glance

- **Clean Architecture**, enforced at build time — `RetailFlow.Domain` has zero
  dependencies, and [`RetailFlow.ArchitectureTests`](tests/RetailFlow.ArchitectureTests)
  fails the build if any layer starts depending on the wrong thing.
- **CQRS**: `RetailFlow.Reporting` is a genuinely separate read side (its own
  `DbContext`, no reference to `RetailFlow.Domain`) rather than the same
  entities with a different label.
- **Transactional outbox**: an aggregate's new state and the domain event it
  raised are written to Postgres in one transaction; publishing to RabbitMQ
  happens afterward, durably, with retry — see
  [ADR-002](docs/ADRs/002-outbox-pattern-for-reliability.md).
- **Wolverine**, not MediatR + MassTransit: both went commercial after the
  original plan was written. One MIT-licensed library now covers in-process
  dispatch, the RabbitMQ transport, Sagas, and the outbox — see
  [ADR-006](docs/ADRs/006-messaging-and-mediator-library-choice.md).
- **Keycloak** for AuthN/OIDC, with policy-based RBAC (`Manager`, `Customer`,
  `AdminOnly`) backed by realm roles.

Full detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). Decision records:
[docs/ADRs](docs/ADRs/README.md).

---

## Getting started

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker
Desktop (or another Docker engine). [`just`](https://github.com/casey/just) is
optional — every recipe in the `justfile` is a plain `dotnet`/`docker` command
underneath.

```bash
just up          # Postgres, RabbitMQ, Redis, Seq, Keycloak
just test        # dotnet test RetailFlow.slnx - includes a real Testcontainers-backed
                  # boot of the API against disposable Postgres/RabbitMQ/Redis
just run-api      # dotnet run --project src/RetailFlow.Api
```

Then:
- API: `http://localhost:5xxx` (see the port `dotnet run` prints), health checks
  at `/health`, `/health/live`, `/health/ready`.
- Seq (logs): `http://localhost:8081`
- RabbitMQ management: `http://localhost:15672` (guest/guest)
- Keycloak admin console: `http://localhost:8080` (admin/admin)

Get a bearer token with one of the seeded demo users
(`admin.demo` / `customer.demo`, password `retailflow-dev`):

```bash
curl -X POST http://localhost:8080/realms/retailflow/protocol/openid-connect/token \
  -d client_id=retailflow-api -d grant_type=password \
  -d username=admin.demo -d password=retailflow-dev
```

Run `just` with no arguments (or open the `justfile`) for the full recipe list.

---

## Project structure

```
RetailFlow/
├── src/
│   ├── RetailFlow.Domain/          # Entities, value objects, domain events. Zero dependencies.
│   ├── RetailFlow.Application/     # Commands, queries, validators. No Wolverine reference.
│   ├── RetailFlow.Infrastructure/  # EF Core, Wolverine wiring, Redis, health checks.
│   ├── RetailFlow.Shared/          # Correlation IDs, clock, Result<T> - cross-cutting, no Domain ref.
│   ├── RetailFlow.Reporting/       # CQRS read side. Own DbContext, no Domain reference.
│   ├── RetailFlow.Api/             # ASP.NET Core minimal API host.
│   └── RetailFlow.Worker/          # Background host (event consumers, projections, sagas).
├── tests/
│   ├── RetailFlow.UnitTests/
│   ├── RetailFlow.IntegrationTests/   # Testcontainers: real Postgres/RabbitMQ/Redis
│   ├── RetailFlow.ArchitectureTests/  # NetArchTest - enforces the dependency rules above
│   ├── RetailFlow.ContractTests/      # Pact - wired, no consumer/provider pair yet
│   ├── RetailFlow.ChaosTests/         # Polly v8 resilience pipelines
│   └── RetailFlow.PerformanceTests/   # BenchmarkDotNet
├── docs/
│   ├── ARCHITECTURE.md
│   └── ADRs/
├── docker/keycloak/realm-export.json
├── docker-compose.yml
├── Directory.Build.props           # Shared project settings
├── Directory.Packages.props        # Central Package Management - one version per package
└── justfile
```

Sales/Inventory/Fiscal/Notification bounded contexts will live as namespaces
inside `Domain`/`Application` initially (not separate assemblies) — see
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#bounded-contexts) for why.

---

## Technology stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10, ASP.NET Core Minimal APIs |
| Persistence | PostgreSQL, EF Core 10 |
| Messaging / CQRS dispatch / Outbox / Saga | [Wolverine](https://wolverinefx.net/) (MIT) |
| Cache | Redis |
| AuthN/AuthZ | Keycloak (OIDC), policy-based RBAC |
| Validation | FluentValidation |
| Logging | Serilog → Seq |
| Resilience | Polly v8 |
| Architecture | Clean Architecture, DDD, CQRS |
| Testing | xUnit, NetArchTest, Testcontainers, PactNet, BenchmarkDotNet |
| Local infra | Docker Compose |
| CI | GitHub Actions |

---

## Roadmap

Everything below Phase 1 is planned, not built — tracked as it lands in
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#status-whats-real-vs-planned) and
[docs/ADRs](docs/ADRs/README.md).

- Sales, Inventory, Fiscal, Notification bounded contexts (actual business logic)
- Saga orchestration for the create-sale flow
- OpenTelemetry distributed tracing, Prometheus metrics, Grafana dashboards
- Audit trail, PII encryption at rest, `docs/SECURITY.md`
- SonarQube / OWASP dependency scanning / Dependabot
- Kubernetes manifests, CI/CD image publishing

---

## License

MIT — see [LICENSE](LICENSE).
