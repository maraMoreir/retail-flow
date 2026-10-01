# ADR-0007 — Estoque como ledger append-only, com snapshot e reserva efêmera

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

O modelo intuitivo de estoque é uma tabela `stock (sku, store_id, quantity)` atualizada
com `UPDATE stock SET quantity = quantity - 1 WHERE sku = ? AND store_id = ?`.

Esse modelo tem dois defeitos sérios em escala de varejo:

**Contenção.** Todo `UPDATE` na mesma linha serializa. Num SKU em promoção na Black
Friday, milhares de vendas simultâneas viram uma fila de espera pelo lock daquela linha —
e essa linha vira o gargalo do sistema inteiro, independentemente de quantos pods de API
existam.

**Perda de informação.** O `UPDATE` destrói o histórico. Quando o inventário físico diverge
do sistema (e diverge sempre), não há como reconstruir o que aconteceu. Inventário sem
rastro de movimentação é inauditável.

## Decisão

**Ledger append-only.** Nenhum `UPDATE` de saldo. Toda alteração é um `INSERT`:

```sql
create table inventory.movements (
  id            bigserial,
  occurred_at   timestamptz not null default now(),
  store_id      uuid        not null,
  sku           text        not null,
  kind          text        not null,   -- sale | purchase | adjustment |
                                        -- transfer_in | transfer_out | return | loss
  quantity      numeric(18,4) not null, -- sinalizado: negativo para saída
  reference_id  uuid        not null,   -- venda, nota de entrada, ordem de transferência
  reason        text,
  created_by    uuid
) partition by range (occurred_at);
create index on inventory.movements (store_id, sku, occurred_at);
```

**Snapshot consolidado.** Um projetor consolida periodicamente o saldo por
`(store_id, sku)`, guardando o `id` do último movimento incluído:

```sql
create table inventory.snapshots (
  store_id           uuid not null,
  sku                text not null,
  quantity           numeric(18,4) not null,
  last_movement_id   bigint not null,
  updated_at         timestamptz not null,
  primary key (store_id, sku)
);
```

**Saldo disponível** = `snapshot.quantity` + soma dos movimentos com
`id > last_movement_id` − reservas ativas.

**Reserva no Redis com TTL.** Reserva é efêmera e de alta rotatividade — chave
`{tenant}:resv:{store}:{sku}`, com TTL alinhado ao tempo máximo de uma venda em aberto.
Confirmar a venda converte a reserva em movimento no ledger e apaga a chave.

O prefixo de tenant entra desde o dia 0 mesmo com um tenant só. Chave de cache sem
prefixo é colisão entre clientes no dia da migração, e reescrever o esquema de chaves com
o cache quente em produção significa invalidar tudo de uma vez
([ADR-0014](ADR-0014-tenantid-dormente.md)).

## Por quê

**O ledger elimina a contenção porque `INSERT` não compete por lock de linha.** Cada venda
grava sua própria linha. O throughput passa a ser limitado por I/O de escrita, que escala,
em vez de por lock de linha, que não escala.

**O snapshot resolve o custo de leitura.** Somar o ledger inteiro a cada consulta seria
inviável com centenas de milhões de movimentos. Com snapshot, a leitura é uma linha mais
um punhado de movimentos recentes — barato e previsível. A frequência do snapshot é o
dial: mais frequente = leitura mais barata, escrita mais cara.

**A auditabilidade vem de graça.** O ledger é exatamente o que uma auditoria de inventário
precisa: quem, quando, quanto, por quê e referenciando o quê. No modelo `UPDATE`, isso
exigiria uma tabela de histórico separada — que é o mesmo ledger, só que redundante e
passível de divergir do saldo.

**Por que reserva no Redis e não no Postgres.** Reserva tem vida de segundos a minutos e
rotatividade altíssima. Uma tabela para isso teria mais `DELETE` que `SELECT` e viraria
fonte de bloat. E se um processo morre no meio, o TTL libera sozinho — sem job de limpeza,
que é um mecanismo a menos para falhar.

**A consequência de a reserva não ser transacional com o ledger** é aceita deliberadamente:
o pior caso é uma reserva órfã que expira sozinha, não um estoque errado. O contrário —
estoque autoritativo no Redis — seria inaceitável.

## Alternativas descartadas

- **`UPDATE` com concorrência otimista e retry.** Rejeitada: sob contenção alta o retry
  amplifica a carga em vez de resolvê-la. Vira livelock no SKU quente.
- **`SELECT ... FOR UPDATE` no saldo.** Rejeitada: serializa explicitamente, que é o
  problema que estamos evitando.
- **Estoque autoritativo no Redis.** Rejeitada: dado financeiro-contábil não pode viver só
  em cache. Redis é o lugar da reserva, não do saldo.
- **Event Sourcing completo do agregado de estoque.** Rejeitada por P2. O ledger entrega o
  benefício relevante (histórico completo e sem contenção) sem o custo operacional do
  event sourcing (versionamento de eventos, replay, snapshots de agregado).

## Consequências

**Positivas**
- Sem contenção em SKU quente — o gargalo vira I/O, que escala.
- Histórico completo e auditável por construção.
- Ajustes e correções são novos movimentos, nunca reescrita do passado.

**Negativas**
- **Leitura de saldo é mais complexa.** Snapshot + delta + reservas, em vez de um `SELECT`.
  Tem que estar encapsulado em `AvailabilityQuery` — se alguém ler o snapshot direto, vai
  ler saldo desatualizado.
- **A tabela cresce muito.** Exige particionamento mensal e arquivamento
  ([ADR-0010](ADR-0010-particionamento-retencao.md)).
- O projetor de snapshot é um componente novo que pode atrasar. Precisa de alerta sobre o
  lag; snapshot atrasado torna a leitura progressivamente mais cara.
- **Estoque negativo é detectado, não impedido** pelo banco. A validação é da aplicação, e
  há uma janela em que uma venda concorrente pode passar do limite. Para SKU com controle
  rígido, a reserva no Redis (`DECRBY` atômico) é a barreira real.
