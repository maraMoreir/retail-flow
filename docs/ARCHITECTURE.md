# RetailFlow Architecture

This describes the system as it actually exists today (Phase 2: Architecture &
Domain Design, building on Phase 1's foundation), not the full target design —
see [Status](#status-whats-real-vs-planned) at the
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
**Sales**, **Inventory**, **Fiscal**, **Notification**, **Reporting**.
`RetailFlow.Reporting` is its own project since it's genuinely different (no
write model at all — see [CQRS](#cqrs--eventual-consistency) below). The other
four now exist as namespaces/folders inside `RetailFlow.Domain`
(`RetailFlow.Domain.Sales`, `.Inventory`, `.Fiscal`, `.Notification`) rather than
as separate assemblies — splitting them into physically separate projects is a
deferred decision, made if/when a context actually needs to deploy or scale
independently, not before. Each namespace currently holds that context's domain
event contracts (see [below](#domain-events)) and any payload-shape records they
carry; the aggregates that will *raise* those events (`Sale`, `Product`, ...)
are Phase 3 work — see [Status](#status-whats-real-vs-planned).

Two value objects are shared kernel, living in `RetailFlow.Domain.Common.ValueObjects`
rather than any one context, because more than one context needs them without
depending on each other: `Money` (amount + currency; Sales prices with it,
Inventory costs with it, Fiscal reports tax amounts with it) and `ProductCode`
(a normalized product identifier every context can reference).

## Domain events

The vocabulary every context communicates through — each a `record` deriving
from `RetailFlow.Domain.Common.DomainEvent`, immutable, past-tense. These are
contracts other bounded contexts and `RetailFlow.Worker` react to; nothing that
raises them exists yet (see [Status](#status-whats-real-vs-planned)).

| Event | Context | Raised when |
|---|---|---|
| `SaleCreatedEvent` | Sales | A sale is opened, before payment |
| `SaleCompletedEvent` | Sales | Payment confirmed — carries the final `Money` total and line items |
| `SaleCancelledEvent` | Sales | A sale is cancelled, directly or as saga compensation |
| `InventoryReservedEvent` | Inventory | Stock held for every line of a sale |
| `InventoryReleasedEvent` | Inventory | A reservation given back (cancellation or compensation) |
| `FiscalGeneratedEvent` | Fiscal | A fiscal document (NFC-e/NF-e) is authorized |
| `NotificationSentEvent` | Notification | A notification is actually dispatched |

See [ADR-001](ADRs/001-saga-pattern-for-transactions.md) for how these chain
together through the create-sale saga's happy and compensating paths.

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

## CQRS & eventual consistency

`RetailFlow.Reporting` is updated asynchronously from the same domain events the
outbox delivers, not synchronously inside the write transaction — see
[ADR-004](ADRs/004-cqrs-eventual-consistency.md) for the full reasoning. Target
consistency window: 2-5 seconds between a write committing and the read model
reflecting it. Two consequences worth knowing before writing a Reporting
projection handler: it must be **idempotent** (the outbox is at-least-once
delivery, not exactly-once — expect to see the same event twice sometimes), and
it must not assume "read your own write" — a client that just completed a sale
may not see it on the dashboard for a few seconds.

## Resilience

Three policies from the project plan, wired at the infrastructure level so
individual handlers don't have to think about them:

| Concern | Policy | Where |
|---|---|---|
| Database | 3 retries, exponential backoff (Npgsql `EnableRetryOnFailure`) | Both `RetailFlowDbContext` and `ReportingDbContext` registrations, in `RetailFlow.Infrastructure`/`RetailFlow.Reporting`'s `DependencyInjection.cs` |
| Message handling | 5 retries, growing cooldown, then Wolverine's error queue | `opts.OnException<Exception>().RetryWithCooldown(...)` in `RetailFlow.Infrastructure/DependencyInjection.cs` |
| Outbound HTTP to external systems | Retry (3x, exponential) → circuit breaker (50% failure ratio, 30s sampling/break) → 30s timeout | [`HttpClientResilienceExtensions.AddRetailFlowResilience()`](../src/RetailFlow.Infrastructure/Resilience/HttpClientResilienceExtensions.cs) — not yet called anywhere, since no outbound HTTP client (e.g. the Fiscal authority) exists yet |

The HTTP pipeline's circuit-breaker behavior is proven against a real (fake-backed)
`HttpClient`, not just configured, in
[`RetailFlow.ChaosTests/HttpClientResilienceTests.cs`](../tests/RetailFlow.ChaosTests/HttpClientResilienceTests.cs).

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
end-to-end infrastructure proof (it boots the real API host against real,
disposable Postgres/RabbitMQ/Redis containers), and
[`RetailFlow.ChaosTests`](../tests/RetailFlow.ChaosTests) for the resilience
policies. What's explicitly **not** built yet:

- Bounded-context aggregates and their behavior (no `Sale`, `Product`, etc. —
  Phase 2 defined the *vocabulary* (events, shared value objects) they'll use;
  Phase 3 builds the aggregates themselves, which is also when the domain events
  above get an actual publisher and the Reporting/Saga mechanisms described here
  get something real to react to).
- The `CreateSaleSaga` class itself (design recorded in
  [ADR-001](ADRs/001-saga-pattern-for-transactions.md); no code yet — there's no
  `SaleCompletedEvent` publisher to trigger it).
- Any `RetailFlow.Reporting` projection handler (design recorded in
  [ADR-004](ADRs/004-cqrs-eventual-consistency.md); same reason).
- OpenTelemetry/Prometheus/Grafana observability.
- SonarQube, OWASP dependency scanning, Dependabot, issue/PR templates — skipped
  for this pass since they need external accounts/tokens; the CI workflow that
  exists (`.github/workflows/ci.yml`) is restore/build/test only.
- `docs/SECURITY.md`, `docs/TESTING.md`, `docs/DEPLOYMENT.md`, `docs/API.md`,
  runbooks — this file and the ADRs above are the documentation footprint for now.
