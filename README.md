# RetailFlow

> A production-inspired distributed retail platform built with .NET 10, demonstrating modern enterprise architecture, event-driven communication, parallel processing, batch operations, and full observability.

![.NET](https://img.shields.io/badge/.NET-10-blueviolet)
![RabbitMQ](https://img.shields.io/badge/RabbitMQ-Message_Broker-orange)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-Database-blue)
![Redis](https://img.shields.io/badge/Redis-Cache-red)
![Docker](https://img.shields.io/badge/Docker-Ready-2496ED)
![License](https://img.shields.io/badge/License-MIT-green)

---

# Overview

RetailFlow is an enterprise retail platform inspired by real-world Point of Sale (POS) and ERP systems.

The solution demonstrates how modern retail systems handle sales, inventory, fiscal operations, notifications, reporting, and batch imports using distributed services and asynchronous messaging.

Instead of building a simple CRUD application, RetailFlow focuses on production-ready backend architecture, scalability, resiliency, and observability.

---

# Features

- Sales Management
- Inventory Control
- Fiscal Processing (NFC-e / NF-e simulation)
- Batch Import
- RabbitMQ Event Bus
- Parallel Processing
- Distributed Workers
- Real-time Monitoring
- Dashboard
- JWT Authentication
- OpenTelemetry
- Prometheus
- Grafana
- Health Checks
- Docker
- CI/CD

---

# System Architecture

```

+------------------------+
\| POS / Web Application |
+-----------+------------+
|
v
+------------------------+
\| Retail API Gateway |
+-----------+------------+
|
+----------------+----------------+
| |
v v

Sales Inventory
Service Service

| |

+----------------+----------------+
|
RabbitMQ Event Bus
|
+--------+---------+---------+---------+
| | | |
v v v v

Fiscal Notification Reporting Worker
Service Service Service Service

|
v

PostgreSQL

|
v

Admin Dashboard

```

---

# Services

## Sales API

Responsible for sales operations.

### Responsibilities

- Create sales
- Apply discounts
- Calculate totals
- Register payments
- Publish SaleCompleted events

---

## Inventory Service

Responsible for stock management.

### Responsibilities

- Reserve products
- Update inventory
- Stock adjustments
- Product availability

---

## Fiscal Service

Simulates the fiscal workflow found in enterprise systems.

### Responsibilities

- Generate NFC-e
- Generate NF-e
- Fiscal validation
- XML generation
- Authorization simulation

---

## Notification Service

Handles asynchronous notifications.

### Responsibilities

- Email
- SMS (future)
- Push notifications
- Webhooks

---

## Reporting Service

Read model optimized for dashboards.

### Responsibilities

- Sales KPIs
- Revenue
- Products
- Inventory indicators
- Daily reports

---

## Batch Import Service

Designed to process very large datasets.

Supports importing:

- Products
- Customers
- Prices
- Inventory
- Suppliers

Imports are automatically divided into chunks and processed in parallel.

---

## Worker Service

Consumes RabbitMQ events.

Processes:

- Inventory updates
- Fiscal generation
- Notifications
- Reporting
- Audit logs

Workers are stateless and horizontally scalable.

---

## Admin Dashboard

Centralized operational dashboard.

Features:

- Batch progress
- Queue monitoring
- Worker status
- Health Checks
- Metrics
- Distributed tracing
- Logs

---

# Event Flow

```

Sale Created

↓

Sales API

↓

SaleCompleted Event

↓

RabbitMQ

↓

+-------------+-------------+--------------+

Inventory Fiscal Reporting

Worker Worker Worker

↓

Database

↓

Dashboard

```

---

# Batch Processing

RetailFlow supports importing hundreds of thousands of records.

```

CSV

↓

Batch Import

↓

Split

↓

RabbitMQ

↓

Parallel Workers

↓

Database

```

Parallel processing uses:

- Parallel.ForEachAsync
- Channels
- SemaphoreSlim

---

# Health Checks

RetailFlow exposes production-ready endpoints.

```

/health

/health/live

/health/ready

```

Checks include:

- PostgreSQL
- RabbitMQ
- Redis
- Worker availability
- Disk
- Memory

---

# Observability

## Metrics

- Sales per minute
- Queue length
- Processing time
- Failed messages
- Inventory updates
- Worker throughput

Powered by:

- Prometheus
- Grafana

---

## Distributed Tracing

Every request shares the same TraceId across:

- API
- RabbitMQ
- Workers
- Database

Powered by OpenTelemetry.

---

## Logging

Structured logs with:

- TraceId
- UserId
- SaleId
- WorkerId
- Processing duration

Visualized using Seq.

---

# Technology Stack

## Backend

- .NET 10
- ASP.NET Core
- Minimal APIs
- Entity Framework Core
- PostgreSQL
- RabbitMQ
- Redis

## Architecture

- Clean Architecture
- DDD
- CQRS
- SOLID
- Event Driven Architecture
- Vertical Slice Architecture

## Infrastructure

- Docker
- Docker Compose
- GitHub Actions
- OpenTelemetry
- Prometheus
- Grafana
- Serilog
- Polly

---

# Project Structure

```

src/

RetailFlow.Api

RetailFlow.Domain

RetailFlow.Application

RetailFlow.Infrastructure

RetailFlow.Workers

RetailFlow.Reporting

RetailFlow.BatchImport

RetailFlow.Admin

tests/

RetailFlow.UnitTests

RetailFlow.IntegrationTests

RetailFlow.ArchitectureTests

RetailFlow.PerformanceTests

```

---

# Future Roadmap

- Payment Gateway
- Pix Integration
- Loyalty Program
- Coupons
- Promotions Engine
- Multi-store Support
- Multi-tenancy
- Event Sourcing
- Kubernetes Deployment
- Saga Pattern
- Outbox Pattern
- Cache Invalidation
- Elasticsearch

---

# Design Principles

- Clean Architecture
- Domain Driven Design
- Event Driven Architecture
- High Cohesion
- Low Coupling
- SOLID
- Production First
- Cloud Ready

---

# License

MIT
