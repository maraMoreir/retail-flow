# ADR-0003 — Outbox transacional na publicação, Inbox na recepção

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

O fluxo descrito originalmente é: a Api grava a venda no PostgreSQL e publica
`SaleCompleted` no RabbitMQ. São **dois recursos transacionais distintos sem transação
comum**. Existe uma janela entre o `COMMIT` e o `basic.publish` em que o processo pode
morrer, a rede pode falhar ou o broker pode estar indisponível.

Os dois modos de falha:

- **Commit sem publish:** a venda existe, o estoque nunca baixa, a nota nunca é emitida,
  o relatório nunca vê a venda. Silencioso — ninguém percebe até o inventário divergir.
- **Publish sem commit:** publica e a transação sofre rollback. O estoque baixa de uma
  venda que não existe.

O Outbox estava listado no README como "Future Roadmap".

## Decisão

**Outbox.** Toda publicação de evento de integração é um `INSERT` em
`<schema>.outbox_messages` **dentro da mesma transação** que persiste o agregado. Nenhum
código de domínio chama o broker diretamente.

```sql
create table sales.outbox_messages (
  id            bigserial   primary key,
  tenant_id     uuid        not null,
  occurred_at   timestamptz not null default now(),
  aggregate_id  uuid        not null,
  type          text        not null,   -- 'retail.sales.completed.v1'
  payload       jsonb       not null,
  trace_parent  text,                   -- propaga o traceId W3C
  processed_at  timestamptz,
  attempts      int         not null default 0
);
create index on sales.outbox_messages (processed_at, id) where processed_at is null;
```

`tenant_id` na linha, e não só dentro do `payload`, é o que permite depois ler a outbox
com justiça entre tenants — sem isso, uma carga em massa de um tenant atrasa os eventos
de venda de todos os outros ([ADR-0014](ADR-0014-tenantid-dormente.md)).

`RetailFlow.OutboxRelay` faz polling com `SELECT ... FOR UPDATE SKIP LOCKED`, publica com
publisher confirms e marca `processed_at`. `SKIP LOCKED` permite N réplicas do relay sem
duplicar trabalho nem travar umas às outras.

**Inbox.** Todo consumidor registra o `message_id` processado em `<schema>.inbox_messages`
**na mesma transação do efeito colateral**. Mensagem já vista é descartada e confirmada.

```sql
create table inventory.inbox_messages (
  message_id   uuid        primary key,
  tenant_id    uuid        not null,
  consumer     text        not null,
  processed_at timestamptz not null default now()
);
```

Aqui `message_id` sozinho basta como chave: ele é gerado pelo **nosso** publicador, então
é globalmente único por construção — diferente da chave de idempotência do
[ADR-0004](ADR-0004-idempotencia-pdv.md), que vem do cliente. `tenant_id` entra como
coluna mesmo assim, para expurgo e particionamento por tenant.

## Por quê

**Outbox é a única solução correta** para o dual-write sem transação distribuída. As
alternativas não resolvem:

- *Retry na publicação* não ajuda se o processo morreu — não há quem tente de novo.
- *Publicar antes do commit* inverte o problema, não o elimina.
- *Two-phase commit* entre PostgreSQL e RabbitMQ existe no papel, mas custa disponibilidade
  (coordenador é ponto único e transações em dúvida travam recursos) e o suporte no
  ecossistema .NET é ruim.

**Inbox é obrigatório porque Outbox garante at-least-once, não exactly-once.** O relay pode
publicar e morrer antes de marcar `processed_at`; o broker pode reentregar. Sem dedupe no
consumidor, a mensagem duplicada baixa estoque duas vezes. Publicar ao menos uma vez é
inútil se o consumidor não souber lidar com "mais de uma".

**Por que o relay é processo separado:** polling contínuo tem padrão de acesso ao banco
diferente do da Api, e a cardinalidade desejada é diferente (poucas réplicas, fixas). Dentro
da Api, cada pod novo viraria mais um poller competindo pela mesma tabela.

**`trace_parent` na tabela** é o que mantém o traceId através do broker. Sem isso o trace
distribuído quebra exatamente na fronteira assíncrona — que é onde mais se precisa dele.

## Alternativas descartadas

- **Change Data Capture (Debezium + logical decoding).** Tecnicamente superior: sem polling,
  latência menor. Rejeitada por ora porque adiciona Kafka Connect e uma peça operacional
  inteira ao stack. Reconsiderar se a latência do polling incomodar.
- **Publicar direto com retry e log de falha.** Rejeitada: não cobre morte do processo.
- **`TransactionScope` distribuído.** Rejeitada: ver acima.

## Consequências

**Positivas**
- Nenhum evento é perdido, e nenhum evento é publicado para uma transação revertida.
- A tabela de outbox é um log auditável natural do que o sistema afirmou ter acontecido.

**Negativas**
- **Latência extra**: o evento só sai no próximo ciclo de polling. Com intervalo de 200ms,
  adiciona até 200ms ao fluxo assíncrono. Aceitável porque nada no caminho síncrono depende disso.
- A tabela de outbox cresce rápido e precisa de expurgo — particionada e com job de limpeza,
  senão vira a maior tabela do banco em semanas.
- Escrita amplificada: cada venda escreve uma linha a mais.
- Ordem **não** é garantida entre agregados diferentes. Consumidores precisam ser
  comutativos ou usar o número de versão do agregado.
