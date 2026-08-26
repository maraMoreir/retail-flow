# RetailFlow Architecture

This describes the system as it actually exists today (Phase 1: Foundation), not
the full target design — see [Status](#status-whats-real-vs-planned) at the
bottom for exactly where the line is. For *why* particular decisions were made,
see [docs/ADRs](ADRs/README.md); this document is the map, the ADRs are the
reasoning.

## Shape of the system

RetailFlow is a modular monolith organized as bounded contexts sharing one Clean
Architecture core, not (yet) a set of independently deployable services. Two
hosts run today:

- **RetailFlow.Api** — the HTTP entry point (ASP.NET Core minimal APIs).
- **RetailFlow.Worker** — a background host with no HTTP surface. Its job is
  everything asynchronous: consuming domain events over RabbitMQ, projecting
  read models, handling sagas. In Phase 1 it exists and is wired to the same
  infrastructure as the API, but has no consumers yet — Wolverine's own hosted
  service is what keeps it alive (see [`Program.cs`](../src/RetailFlow.Worker/Program.cs)).

Both hosts are built from the same set of class libraries:

```
                     ┌──────────────┐  ┌───────────────┐
                     │ RetailFlow   │  │ RetailFlow    │
                     │ .Api         │  │ .Worker       │
                     └──────┬───────┘  └───────┬───────┘
                            │                  │
              ┌─────────────┴─────────┬────────┘
              ▼                       ▼
     ┌─────────────────┐   ┌──────────────────────┐
     │ RetailFlow       │   │ RetailFlow            │
     │ .Reporting       │   │ .Infrastructure       │
     │ (CQRS read side) │   │ (EF Core, Wolverine,  │
     └────────┬─────────┘   │  Redis, health checks)│
              │              └───────────┬───────────┘
              │                          ▼
              │               ┌──────────────────────┐
              │               │ RetailFlow.Application│
              │               │ (commands, queries,   │
              │               │  validators)          │
              │               └───────────┬───────────┘
              │                           ▼
              │               ┌──────────────────────┐
              └──────────────▶│ RetailFlow.Shared     │◀──── RetailFlow.Domain
                              │ (correlation, clock,  │      (entities, value
                              │  Result<T>)           │       objects, events -
                              └──────────────────────┘       ZERO dependencies)
```

Arrows point from "depends on" to "depended upon." `RetailFlow.Domain` has no
project references at all — enforced by
[`RetailFlow.ArchitectureTests`](../tests/RetailFlow.ArchitectureTests/LayerDependencyTests.cs),
which fails the build if that (or any other rule on this diagram) is violated.
`RetailFlow.Reporting` deliberately does **not** reference `RetailFlow.Domain`:
it's the CQRS read side, querying its own denormalized projections through a
separate, read-only `DbContext` — never the write-side aggregates.

## Bounded contexts

The target design (per the original project plan) is five bounded contexts:
**Sales**, **Inventory**, **Fiscal**, **Notification**, **Reporting**. Phase 1
only stands up the shared kernel (`RetailFlow.Domain`/`Application`) they'll all
build on, plus `RetailFlow.Reporting` as its own project since it's genuinely
different (no write model at all). The other four contexts will live as
namespaces/folders inside `Domain`/`Application` initially (e.g.
`RetailFlow.Domain.Sales`) rather than as separate assemblies — splitting them
into physically separate projects is a deferred decision, made if/when a context
actually needs to deploy or scale independently, not before.

## Messaging: Wolverine

One library covers in-process command/query dispatch, the RabbitMQ transport,
and the transactional outbox/inbox — see
[ADR-006](ADRs/006-messaging-and-mediator-library-choice.md) for why (short
version: the plan's original choices, MediatR and MassTransit, both moved to
commercial licensing).

- **Dispatch**: handlers are plain classes Wolverine finds by naming convention
  (`XHandler.Handle(X command)`) in the `RetailFlow.Application` assembly — no
  interface to implement, no package reference to Wolverine from Application
  itself.
- **Outbox/inbox**: see [ADR-002](ADRs/002-outbox-pattern-for-reliability.md).
  Writing an aggregate and publishing the event it raised happen in one Postgres
  transaction; Wolverine relays to RabbitMQ afterward, durably, with retry.
- **Validation**: `opts.UseFluentValidation()` wraps every handler automatically
  — a `FluentValidation.AbstractValidator<T>` for a command/query is discovered
  and run before the handler, no per-handler wiring needed. A validation failure
  becomes a 400 with a field-level error breakdown (see
  [`GlobalExceptionHandler`](../src/RetailFlow.Api/ErrorHandling/GlobalExceptionHandler.cs)).

All of this is configured once, in
[`RetailFlow.Infrastructure/DependencyInjection.cs`](../src/RetailFlow.Infrastructure/DependencyInjection.cs),
via `AddRetailFlowInfrastructure(this IHostApplicationBuilder builder)` — called
identically from both `RetailFlow.Api` and `RetailFlow.Worker`'s `Program.cs`, so
the two hosts cannot drift out of sync on how they talk to Postgres/RabbitMQ/Redis.

## Security

- **AuthN**: JWT bearer tokens issued by Keycloak (realm `retailflow`, client
  `retailflow-api` — see [`docker/keycloak/realm-export.json`](../docker/keycloak/realm-export.json)).
  `Keycloak:Authority` / `Keycloak:Audience` in configuration point the API's
  `AddJwtBearer()` at it.
- **AuthZ**: policy-based (`Manager`, `Customer`, `AdminOnly`), backed by realm
  roles. Keycloak puts realm roles inside a nested `realm_access.roles` claim
  rather than individual role claims, so
  [`KeycloakRoleClaimsTransformation`](../src/RetailFlow.Api/Authentication/KeycloakRoleClaimsTransformation.cs)
  unpacks that into standard `ClaimTypes.Role` claims once per authenticated
  request — without it, `RequireRole()`/`[Authorize(Roles = ...)]` would silently
  never match anything.
- **Not yet built**: encryption-at-rest for PII, an audit trail, and a real
  threat model / `docs/SECURITY.md` — tracked for a later pass, alongside
  ADR-005.

## Observability

- **Structured logging**: Serilog, console + Seq, with a two-stage bootstrap
  (a minimal logger catches failures before configuration/DI exist; the real one
  takes over once they do). Every log line is enriched with machine name, thread
  ID, application/environment name, and — inside a request — a correlation ID.
- **Correlation IDs**:
  [`CorrelationIdMiddleware`](../src/RetailFlow.Api/Middleware/CorrelationIdMiddleware.cs)
  reads an inbound `X-Correlation-Id` header (or mints one), echoes it back on the
  response, and pushes it into both Serilog's `LogContext` and
  [`AmbientCorrelationIdProvider`](../src/RetailFlow.Shared/Correlation/ICorrelationIdProvider.cs)
  (an `AsyncLocal`, readable from anywhere — including deep in domain code that
  has no access to `HttpContext`).
- **Health checks**: three endpoints with different semantics —
  `/health/live` (is the process up — zero dependency checks, always cheap),
  `/health/ready` (can this instance actually serve traffic — Postgres, RabbitMQ,
  Redis all reachable), `/health` (everything, for manual debugging). See
  [`Program.cs`](../src/RetailFlow.Api/Program.cs).
- **Not yet built**: OpenTelemetry distributed tracing, Prometheus metrics,
  Grafana dashboards — Phase 7 in the original plan.

## Data

PostgreSQL only (no DynamoDB/SQL Server — see the project's confirmed decisions).
Package versions are pinned once for the whole solution via
[Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management)
(`Directory.Packages.props`); individual `.csproj` files reference packages
without a version. `RetailFlowDbContext` (write side) and `ReportingDbContext`
(read side, no `Domain` reference) are separate `DbContext`s against the same
database — no EF entities exist yet, so there are no migrations yet either; that
lands with the first aggregate.

## Local development

```bash
just up          # Postgres, RabbitMQ, Redis, Seq, Keycloak (docker compose)
just run-api      # or: dotnet run --project src/RetailFlow.Api
just run-worker    # or: dotnet run --project src/RetailFlow.Worker
just test         # or: dotnet test RetailFlow.slnx
```

`docker-compose.yml` deliberately does **not** containerize the API/Worker
themselves — `dotnet run` against the containerized dependencies is a much
faster inner loop than rebuilding images on every change. `Dockerfile`s for both
exist ([Api](../src/RetailFlow.Api/Dockerfile),
[Worker](../src/RetailFlow.Worker/Dockerfile)) for when that changes (Phase 9).

To get a bearer token for local testing (demo users seeded by the realm export:
`admin.demo` / `customer.demo`, password `retailflow-dev` for both):

```bash
curl -X POST http://localhost:8080/realms/retailflow/protocol/openid-connect/token \
  -d client_id=retailflow-api -d grant_type=password \
  -d username=admin.demo -d password=retailflow-dev
```

Seq's UI is at `http://localhost:8081`, RabbitMQ's management UI at
`http://localhost:15672` (guest/guest), Keycloak's admin console at
`http://localhost:8080` (admin/admin).

## Status: what's real vs. planned

Everything above this line describes code that exists, builds, and is tested —
see [`RetailFlow.IntegrationTests`](../tests/RetailFlow.IntegrationTests) for the
end-to-end proof (it boots the real API host against real, disposable
Postgres/RabbitMQ/Redis containers). What's explicitly **not** built yet:

- Any bounded-context business logic (no `Sale`, `Product`, etc. — Phase 1 is the
  foundation they'll sit on, not the contexts themselves).
- Saga orchestration (no multi-step distributed transaction exists yet to
  orchestrate).
- OpenTelemetry/Prometheus/Grafana observability.
- SonarQube, OWASP dependency scanning, Dependabot, issue/PR templates — skipped
  for this pass since they need external accounts/tokens; the CI workflow that
  exists (`.github/workflows/ci.yml`) is restore/build/test only.
- `docs/SECURITY.md`, `docs/TESTING.md`, `docs/DEPLOYMENT.md`, `docs/API.md`,
  runbooks — this file and the two ADRs above are the documentation footprint
  for now.
