# ADR-0008 — RabbitMQ com quorum queues; sem Kafka, sem Event Sourcing

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

O README dizia apenas "RabbitMQ Event Bus", sem especificar topologia — que é exatamente
onde sistemas de mensageria quebram em produção. Ao mesmo tempo, listava "Event Sourcing"
no roadmap, o que mudaria o backbone.

A premissa P2 resolveu a segunda questão: **sem Event Sourcing no core**. Isso decide o
broker.

## Decisão

### Broker

**RabbitMQ com quorum queues.** Classic mirrored queues não são opção — estão
descontinuadas e se comportam mal sob partição de rede.

### Topologia

- Um exchange `topic` por contexto publicador: `retail.sales`, `retail.inventory`,
  `retail.fiscal`, `retail.catalog`.
- Routing key com versão: `sales.completed.v1`, `catalog.products.changed.v1`.
- Uma fila por **par (consumidor, evento)**, nunca fila compartilhada entre consumidores
  diferentes. Cada consumidor tem seu próprio ritmo e sua própria DLQ.

### Retry e poison message

Cada fila de trabalho tem uma fila de retry e uma DLQ:

```
retail.sales → [q] inventory.sale-completed
                 ↓ nack
               [q] inventory.sale-completed.retry   (TTL 30s, DLX → fila original)
                 ↓ após N tentativas
               [q] inventory.sale-completed.dlq     (sem consumidor; alerta + Admin)
```

- Máximo de 5 tentativas, backoff por TTL crescente nas filas de retry.
- Mensagem na DLQ **gera alerta** e aparece no Admin com o payload e o erro.
- Nenhuma mensagem é descartada silenciosamente.

### Prioridade

**Por fila separada, não por message priority.** Import em massa publica em
`*.low` consumida por um pool de workers com teto de réplicas baixo.

### Configuração de consumo

- `prefetch` explícito por consumidor (default global é inadequado: um worker puxa
  centenas de mensagens e os outros ficam ociosos).
- Publisher confirms sempre ativos no OutboxRelay.
- Consumo manual de ack, ack após o commit do efeito.

### Contratos

`RetailFlow.Contracts` como pacote versionado, com envelope no formato CloudEvents.
Regra de evolução: **só adicionar campos opcionais**. Mudança incompatível cria `.v2`,
e as duas versões convivem até todos os consumidores migrarem.

## Por quê

**Por que RabbitMQ e não Kafka.** O padrão de uso aqui é *distribuição de trabalho*:
uma mensagem, um consumidor a processa, com ack, retry e DLQ por mensagem. RabbitMQ faz
isso nativamente e bem. Kafka faz mal — reprocessar uma única mensagem envenenada no meio
de uma partição exige mover offset manualmente ou construir uma DLQ por fora.

O que **forçaria** Kafka seria: ordenação estrita por chave de agregado, replay histórico
completo, ou alimentar projeções de Event Sourcing. P2 elimina os três. Rodar Kafka sem
precisar dessas propriedades adiciona ZooKeeper/KRaft, gestão de partições, rebalanceamento
de consumer group e retenção de log — custo operacional considerável sem contrapartida.

**Por que quorum queues.** Classic mirrored queues foram descontinuadas e têm
comportamento problemático em split-brain. Quorum queues usam Raft, com semântica de
replicação previsível.

**Por que não rodar os dois.** É tentador usar RabbitMQ para comandos e Kafka para eventos.
São dois clusters para operar, dois modelos de falha, dois conjuntos de biblioteca. Só
vale quando um deles é insuficiente — e não é o caso.

**Por que prioridade por fila separada e não `x-max-priority`.** Prioridade dentro da fila
não isola recurso: uma mensagem de baixa prioridade já em processamento continua ocupando
o worker e a conexão de banco. Fila separada com pool separado isola de verdade
([ADR-0011](ADR-0011-pgbouncer-isolamento-pools.md)).

**Por que a DLQ não pode ser silenciosa.** DLQ sem alerta é um buraco onde vendas somem
sem ninguém saber. O critério é: toda mensagem que chega na DLQ tem um humano
responsável e aparece no Admin.

## Alternativas descartadas

- **Kafka / Redpanda como backbone.** Rejeitada por P2 (ver acima). **Reavaliar se P2
  mudar** — Event Sourcing sem log ordenado e com replay é muito mais difícil.
- **Azure Service Bus / Amazon SQS+SNS.** Não rejeitadas tecnicamente; reduziriam carga
  operacional. Rejeitadas por acoplamento a nuvem específica, dado que o Edge precisa
  funcionar em qualquer topologia.
- **Ambos os brokers.** Rejeitada: custo operacional dobrado sem ganho.
- **`x-max-priority` nas filas.** Rejeitada: não isola recurso.

## Consequências

**Positivas**
- Um broker, um modelo mental, um conjunto de runbooks.
- Retry e DLQ por mensagem são nativos — não precisam ser construídos.
- Quorum queues sobrevivem à perda de um nó sem perda de mensagem confirmada.

**Negativas**
- **Sem replay histórico.** Reconstruir uma projeção de reporting do zero exige reler o
  banco, não o broker. Aceitável porque o estado atual está no Postgres (P2), mas é uma
  limitação real.
- Quorum queues têm custo de escrita maior que classic queues (replicação Raft, tudo em
  disco). Dimensionar disco e IOPS do cluster com folga.
- **Sem ordenação global.** Consumidores precisam ser comutativos ou usar a versão do
  agregado para descartar eventos fora de ordem.
- Contratos versionados exigem disciplina. Sem teste de contrato
  (`tests/RetailFlow.ContractTests`), a regra de compatibilidade é só uma intenção.
