# Architecture Decision Records

One file per significant, hard-to-reverse decision — not a design doc, a record of
*why* something is the way it is, so nobody has to reconstruct the reasoning (or
re-litigate it) from Slack history later.

> ### Two sets live here, on purpose
>
> **`NNN-*`** — decisions tied to code that exists. Numbering matches the original
> project plan and is stable once assigned.
>
> **`ADR-00NN-*`** — the product architecture, written as a design pass before the code
> that implements it. Portuguese, because the fiscal domain does not translate cleanly.
>
> **They overlap on three topics** (outbox, saga, CQRS). Where they disagree, the
> `ADR-00NN` set is newer and reflects the scope defined in
> [ADR-0020](ADR-0020-escopo-pdv-multi-erp.md): RetailFlow is a POS that integrates with
> the customer's ERP, not a retail platform. Reconciling the two sets into one numbering
> is pending.

---

## Implementation decisions

| # | Title | Status |
|---|-------|--------|
| [001](001-saga-pattern-for-transactions.md) | Saga pattern for distributed transactions | Accepted (design only — no `Sale` aggregate exists yet to trigger it) |
| [002](002-outbox-pattern-for-reliability.md) | Outbox pattern for reliable event delivery | Accepted, implemented |
| [004](004-cqrs-eventual-consistency.md) | CQRS eventual consistency | Accepted (mechanism implemented; no projection exists yet to measure against) |
| [006](006-messaging-and-mediator-library-choice.md) | Messaging & mediator library choice (Wolverine) | Accepted, implemented |
| 003 | Event sourcing consideration | Not written — and [ADR-0008](ADR-0008-rabbitmq-sem-kafka.md) now argues against it |
| 005 | Security strategy | Partially answered by [ADR-0019](ADR-0019-ciclo-de-vida-certificado-a1.md), [ADR-0026](ADR-0026-identidade-do-edge.md) and [ADR-0027](ADR-0027-dados-pessoais-e-retencao.md) |

Numbering is stable once assigned (matches the original project plan) but not
necessarily sequential in *arrival* order — 002 and 006 landed in Phase 1
because the outbox/messaging plumbing was needed immediately; 001 and 004 landed
in Phase 2 as domain-design decisions.

---

## Product architecture

Diagrams: [retail-flow.drawio](../../retail-flow.drawio) ·
Model: [architecture-c4.md](../DesignDocs/architecture-c4.md) ·
Schema: [schema-v1.sql](../DesignDocs/schema-v1.sql)

### Premises and constraints

> **`P` = Premise** — a business decision, revisitable. Changing one changes the architecture.
> **`R` = Constraint** — a fact about the world. Not negotiable; design around it.

| # | |
|---|---|
| P1 | The store must keep selling when the link drops |
| P2 | No Event Sourcing in the core |
| P3 | The customer's ERP owns catalog, price and stock — the POS replicates and reports |
| P4 | Market product: it has to speak to several ERPs |
| R1 | SEFAZ and the acquirer are slow, unstable third parties |
| R2 | The ERP goes down too. A sale never depends on it in real time |

### Scope — read first

| ADR | Decision |
|---|---|
| [0020](ADR-0020-escopo-pdv-multi-erp.md) | **POS, not a retail platform** — reframes 0001–0019 |
| [0021](ADR-0021-erp-connector-anticorrupcao.md) | ERP Connector: anticorruption with a canonical model |

### Structure

| ADR | Decision |
|---|---|
| [0001](ADR-0001-modulos-por-contexto.md) | Organise by bounded context, not by layer |
| [0002](ADR-0002-quatro-deployables.md) | Four deployables at first; extract under pressure |
| [0022](ADR-0022-grafo-de-dependencia-entre-modulos.md) | Module dependency graph |
| [0014](ADR-0014-tenantid-dormente.md) | `TenantId` present and dormant from day 0 |

### Consistency and messaging

| ADR | Decision |
|---|---|
| [0003](ADR-0003-outbox-inbox.md) | Transactional outbox + inbox |
| [0004](ADR-0004-idempotencia-pdv.md) | `Idempotency-Key` required on POS writes |
| [0008](ADR-0008-rabbitmq-sem-kafka.md) | RabbitMQ quorum queues; no Kafka |
| [0016](ADR-0016-saga-venda-compensacao.md) | Sale saga with TTL reservation and compensation |

### Retail domain

| ADR | Decision |
|---|---|
| [0005](ADR-0005-store-edge-operacao-offline.md) | Store Edge: the store sells offline |
| [0006](ADR-0006-fiscal-contexto-isolado.md) | Fiscal split: NFC-e at the Edge, NF-e in the cloud |
| [0007](ADR-0007-estoque-ledger-append-only.md) | Stock as an append-only ledger |
| [0009](ADR-0009-pricing-contexto-cache.md) | Pricing as its own context, cache invalidated by event |
| [0017](ADR-0017-devolucao-troca-cancelamento.md) | Cancellation, return and exchange as distinct flows |
| [0018](ADR-0018-sessao-de-caixa.md) | Mandatory cash session, blind count |
| [0019](ADR-0019-ciclo-de-vida-certificado-a1.md) | A1 certificate lifecycle in stores |

### Data and scale

| ADR | Decision |
|---|---|
| [0010](ADR-0010-particionamento-retencao.md) | Temporal partitioning and retention in PostgreSQL |
| [0011](ADR-0011-pgbouncer-isolamento-pools.md) | PgBouncer and pool isolation per workload |
| [0012](ADR-0012-opensearch-busca-catalogo.md) | ~~OpenSearch for catalog search~~ — removed by 0020 |
| [0015](ADR-0015-import-copy-staging.md) | Binary COPY and staging — now for ERP→POS sync |

### Operational boundaries

Explicit policies at the edges — what lets the architecture survive operations.

| ADR | Decision | Phase |
|---|---|---|
| [0023](ADR-0023-contrato-versionado-edge-cloud.md) | Versioned Edge ↔ Cloud contract | 1 — before code |
| [0024](ADR-0024-integracao-erp-por-capacidade.md) | Capability-based ERP integration | 1 — before code |
| [0025](ADR-0025-atualizacao-frota-edge.md) | Edge fleet update strategy | 1 — before code |
| [0026](ADR-0026-identidade-do-edge.md) | Edge identity and authentication | 2 — before pilot |
| [0028](ADR-0028-evolucao-do-modelo-canonico.md) | Canonical model evolution | 2 — before pilot |
| [0029](ADR-0029-fronteiras-internas-do-store-edge.md) | Store Edge internal boundaries | 2 — before pilot |
| [0027](ADR-0027-dados-pessoais-e-retencao.md) | Personal data, retention and SQLite lifecycle | 3 — before production |
| [0030](ADR-0030-estrategia-de-testes-e-resiliencia.md) | Testing and resilience strategy | 3 — before production |

### Observability

| ADR | Decision |
|---|---|
| [0013](ADR-0013-observabilidade-otlp.md) | Single telemetry pipeline over OTLP; Seq in dev only |

---

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
