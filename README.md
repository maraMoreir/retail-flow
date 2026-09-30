# RetailFlow

> An offline-first **Point of Sale** for Brazilian retail chains, designed to integrate with
> whatever ERP the customer already runs.

![Status](https://img.shields.io/badge/status-DESIGN%20PHASE-orange)
![.NET](https://img.shields.io/badge/.NET-10-blueviolet)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-17-blue)
![RabbitMQ](https://img.shields.io/badge/RabbitMQ-quorum_queues-orange)
![License](https://img.shields.io/badge/License-MIT-green)

---

> ## ⚠️ Status: design, not implementation
>
> `src/` and `tests/` are **empty**. Nothing below is built yet.
>
> What exists today is the architecture: **21 ADRs** and a **C4 model**, covering scope,
> boundaries, data ownership, failure modes and the trade-offs behind each decision.
> That is the deliverable of this phase — the code follows it, not the other way around.
>
> → [Architecture Decision Records](docs/ADRs/README.md) · [C4 diagrams (draw.io)](docs/DesignDocs/RetailFlow-POS-TO-BE.drawio) · [Reference schema](docs/DesignDocs/schema-v1.sql)
>
> *Documentation is written in Portuguese — the target market is Brazilian retail, and the
> fiscal domain does not translate cleanly.*

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

That boundary is the product. See [ADR-0020](docs/ADRs/ADR-0020-escopo-pdv-multi-erp.md).

## What makes it hard

Three things, and they are the whole reason the architecture looks the way it does:

**1. The store must keep selling when the link drops.** Not degrade — sell. That forces a
process running on hardware inside the store (**Store Edge**), a local fiscal number range,
