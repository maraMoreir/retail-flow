# ADR-0001 — Organizar o código por bounded context, não por camada

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

A estrutura inicial do repositório mistura dois eixos de organização incompatíveis:
`RetailFlow.Domain`, `RetailFlow.Application`, `RetailFlow.Infrastructure` (eixo de camada)
convivendo com `RetailFlow.Sales`, `RetailFlow.Inventory`, `RetailFlow.Fiscal` (eixo de
contexto). O README declara simultaneamente "Clean Architecture", "DDD", "CQRS" e
"Vertical Slice Architecture" — as duas últimas são organizadas por funcionalidade, as
duas primeiras por camada.

Na prática, organizar por camada faz cada mudança de funcionalidade tocar quatro projetos,
e não impede acoplamento entre contextos: `Sales.Domain` e `Inventory.Domain` ficam no
mesmo assembly, então nada impede um `using` indevido.

## Decisão

Organizar `src/` por contexto. Cada módulo tem suas próprias camadas internas:

```
src/
  Modules/
    Catalog/      Domain | Application | Infrastructure | Endpoints
    Pricing/
    Sales/
    Inventory/
    Fiscal/
    Payments/
    Cashier/
    Customer/
  Hosts/
    RetailFlow.Api          composition root
    RetailFlow.Workers
    RetailFlow.OutboxRelay
    RetailFlow.BatchImport
    RetailFlow.StoreEdge
  Shared/
    RetailFlow.Contracts    eventos versionados
    RetailFlow.SharedKernel Money, StoreId, TenantId, Cnpj, Result
```

Regras aplicadas:

1. Cada módulo tem **seu próprio schema** no PostgreSQL. Nenhum módulo lê a tabela de outro.
2. Comunicação entre módulos: interface pública explícita (in-process) ou evento de integração.
3. `SharedKernel` contém apenas value objects sem comportamento de negócio de nenhum contexto.
4. Um teste em `ArchitectureTests` falha o build se `Sales` referenciar o interno de `Inventory`.

## Por quê

O critério de decisão é **onde estão os limites que precisam ser defendidos**. Em varejo os
limites são de negócio, não técnicos: as invariantes de precificação não têm nada a ver com
as de estoque, e elas mudam por razões diferentes, em ritmos diferentes.

Camada como eixo primário defende o limite errado. Ninguém nunca precisou impedir que
"Application" chamasse "Infrastructure" em produção — o compilador já resolve isso com
referência de projeto. O que quebra sistemas é `Sales` fazendo `JOIN` direto na tabela de
estoque, e a organização por camada não vê esse acoplamento porque ele acontece dentro de
"Infrastructure".

O schema separado por módulo é o que dá dentes à regra. Sem ele, "módulo" é convenção de
pasta e o primeiro `JOIN` entre contextos apaga a fronteira em silêncio.

## Alternativas descartadas

- **Manter camadas no topo.** Rejeitada: não impede acoplamento entre contextos, que é o
  problema real, e espalha cada mudança por quatro projetos.
- **Vertical slice puro, sem camadas internas.** Rejeitada: funciona bem em CRUD, mas os
  contextos de Sales, Inventory e Fiscal têm invariantes de domínio densas o bastante
  para justificar um modelo isolado da infraestrutura.
- **Microsserviços separados desde já.** Ver [ADR-0002](ADR-0002-quatro-deployables.md).

## Consequências

**Positivas**
- A extração de um módulo em serviço vira mudança de hospedagem, não reescrita: a fronteira
  já existe no código e no banco.
- Teste de arquitetura converte a regra em falha de build, não em revisão de código.

**Negativas**
- Schema por módulo **proíbe `JOIN` entre contextos**. Consultas que hoje seriam um `JOIN`
  viram composição em memória ou um modelo de leitura dedicado. Isso é custo real e recorrente.
- Não há transação distribuída entre schemas. Dentro do mesmo banco ainda há transação
  única, mas isso é uma conveniência temporária que some quando um módulo for extraído — o
  código não deve depender dela. Daí a saga em [ADR-0016](ADR-0016-saga-venda-compensacao.md).
- Curva de aprendizado maior para quem chega esperando a estrutura de camadas convencional.
