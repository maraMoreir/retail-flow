# ADR-0010 — Particionamento temporal e retenção no PostgreSQL desde o schema inicial

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

A premissa P4 fala em "milhões de produtos", mas esse não é o problema. Uma tabela
`catalog.products` com 10 milhões de linhas e índice B-tree em SKU resolve lookup em
tempo sub-milissegundo — não exige tratamento nenhum.

O volume real está no que **cresce sem parar**:

| Tabela | Estimativa (500 lojas, 1.000 vendas/dia) | Ano |
|---|---|---|
| `sales.sales` | 500k linhas/dia | ~180M |
| `sales.sale_items` | ~2,5M linhas/dia | ~900M |
| `inventory.movements` | ~2,5M linhas/dia | ~900M |
| `fiscal.documents` | 500k linhas/dia | ~180M |
| `shared.outbox_messages` | alta rotatividade | — |
| `shared.idempotency_keys` | alta rotatividade | — |

Adicionar particionamento depois, com bilhões de linhas em produção, é uma migração longa
e arriscada. Adicionar no schema inicial é escrever uma cláusula a mais.

## Decisão

**Particionamento declarativo por RANGE em `occurred_at`/`created_at`**, granularidade
mensal, nas tabelas de crescimento contínuo:

```sql
create table sales.sale_items (...) partition by range (created_at);
```

**`pg_partman`** gerencia criação antecipada de partições (3 meses à frente) e
desanexação das antigas. Sem isso, alguém esquece de criar a partição do mês que vem e a
escrita falha à meia-noite do dia 1.

**Chave de partição sempre inclui a coluna temporal**, e toda consulta do caminho quente
filtra por período para permitir *partition pruning*. Consulta sem filtro de data varre
todas as partições — precisa ser detectada em revisão de código.

**Retenção por camada:**

| Dado | No OLTP | Depois |
|---|---|---|
| Vendas e itens | 13 meses (comparação ano a ano) | Parquet em object storage |
| Movimentos de estoque | 13 meses | Parquet |
| Documentos fiscais (metadados) | 5 anos | — (obrigação legal) |
| XML fiscal | — | Object storage, 5 anos ([ADR-0006](ADR-0006-fiscal-contexto-isolado.md)) |
| Outbox processada | 7 dias | Descartada |
| Chaves de idempotência | 7 dias | Descartada |

**Réplicas de leitura** para todo relatório. Nenhuma consulta analítica toca o primary.

**`StoreId` em toda tabela transacional**, desde o início — mesmo com rede única. É a chave
natural de distribuição se um dia for preciso particionar por loja além de por data.

## Por quê

**Por que particionar por data e não por loja.** O padrão de acesso dominante é temporal:
"vendas de hoje", "fechamento do mês", "comparativo com o ano passado". Partição temporal
permite *pruning* nessas consultas e, principalmente, torna o expurgo um `DETACH PARTITION`
— operação de metadados, instantânea — em vez de um `DELETE` de milhões de linhas, que
gera bloat, dispara autovacuum agressivo e degrada o banco por horas.

**Por que mensal e não diário.** Diário daria pruning mais fino, mas produz 365 partições
por ano por tabela; o planejador degrada com milhares de partições e a manutenção fica
pesada. Mensal é o equilíbrio para este volume. Reavaliar se uma partição mensal passar de
~50M linhas.

**Por que `pg_partman` e não um script.** Criação de partição é uma tarefa que, quando
falha, quebra a escrita do sistema inteiro à meia-noite do dia 1 do mês. É exatamente o
tipo de coisa que não deve depender de um cron artesanal.

**Por que 13 meses e não 12.** Comparação ano a ano precisa do mês equivalente do ano
anterior disponível. Com 12 meses exatos, o comparativo de janeiro perde janeiro anterior
no dia 1º.

**Por que Parquet em object storage.** Dado histórico é lido raramente e sempre em varredura
analítica. Parquet colunar comprimido custa uma fração do armazenamento em bloco do banco e
é consultável direto por ferramentas analíticas, sem restaurar para o OLTP.

**Por que `StoreId` em tudo.** Custa 16 bytes por linha agora. Se um dia for preciso
distribuir por loja (sharding ou Citus), a coluna já existe e já está preenchida
historicamente — o que é a parte cara de adicionar depois.

## Alternativas descartadas

- **Sem particionamento, resolver quando doer.** Rejeitada: a migração com bilhões de
  linhas em produção é longa, arriscada e provavelmente exige janela de indisponibilidade.
- **Particionar por hash de `store_id`.** Rejeitada: não ajuda o padrão de acesso temporal
  e torna o expurgo por idade impossível.
- **Arquivar para outro banco relacional.** Rejeitada: custo alto para dado raramente lido.
- **TimescaleDB.** Interessante para a parte de séries temporais, mas adiciona uma extensão
  e um modelo mental para um problema que o particionamento nativo resolve.

## Consequências

**Positivas**
- Expurgo instantâneo por `DETACH PARTITION`.
- `VACUUM` e reindexação operam por partição, em janelas menores.
- Consultas filtradas por período varrem uma fração dos dados.

**Negativas**
- **Índice único precisa incluir a chave de partição.** Isso altera o modelo: chaves
  primárias viram compostas com a coluna temporal. Afeta como o EF Core mapeia os
  agregados e é a maior fricção prática desta decisão.
- **Chave estrangeira apontando para tabela particionada tem limitações.** Algumas
  referências viram validação de aplicação em vez de constraint. Perda real de garantia.
- Consulta sem filtro de data varre tudo e fica mais lenta que numa tabela normal.
  Precisa de disciplina e de detecção em revisão.
- `pg_partman` é uma dependência de extensão a mais para instalar e versionar.
- Arquivamento em Parquet é um pipeline novo para construir, testar e monitorar.
