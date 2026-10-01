# ADR-0015 — Import em massa via COPY binário e tabela de staging

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

O RetailFlow precisa importar catálogos de fornecedor com centenas de milhares de
registros: produtos, clientes, preços, estoque, fornecedores.

O desenho original anunciava `Parallel.ForEachAsync` + `Channels` + `SemaphoreSlim` e EF
Core. Isso parte de um diagnóstico errado: **o gargalo do import não é CPU**. É throughput
de escrita no banco. Paralelizar 64 tarefas contra um pool de 20 conexões produz fila,
contenção e timeout — não velocidade.

E `SaveChangesAsync` do EF Core com change tracking sobre 500 mil entidades é inviável em
memória e em tempo, mesmo com lotes.

## Decisão

**Pipeline com concorrência alinhada ao banco, e COPY binário na escrita.**

```
Object Storage ──stream──> Parse ──Channel──> Validate ──Channel──> Batch
                                                                      │
                                                    COPY binário ─────┤
                                                                      ▼
                                              staging.products_import  (UNLOGGED)
                                                                      │
                                                     MERGE em lotes ──┤
                                                                      ▼
                                                      catalog.products
                                                                      │
                                                                      ▼
                                              ProductsChanged.v1 → invalidação + reindex
```

1. **Leitura em streaming** do object storage. O arquivo nunca é carregado inteiro em
   memória — 500k linhas em CSV podem ser centenas de MB.
2. **`System.Threading.Channels`** liga os estágios com backpressure natural: canal
   limitado, produtor bloqueia quando o consumidor não acompanha.
3. **Uma única dimensão de concorrência**, configurada para ficar **abaixo** do
   `pool_size` do usuário `rf_batch` ([ADR-0011](ADR-0011-pgbouncer-isolamento-pools.md)).
4. **`NpgsqlBinaryImporter`** (`BeginBinaryImport`) grava em tabela de staging `UNLOGGED`.
5. **`MERGE`** do staging para a tabela final, em lotes, com transação por lote.
6. **Evento por lote consolidado**, não por linha — 500 mil eventos individuais afogariam
   o broker e os consumidores.
7. **Progresso incremental persistido**: o job é retomável do último lote confirmado.
8. **Erros por linha vão para uma tabela de rejeição** com o número da linha e o motivo, e
   ficam disponíveis para download no Admin. Import não é tudo-ou-nada.

## Por quê

**Por que COPY e não `INSERT`, nem EF Core.** COPY é o caminho de carga em massa do
PostgreSQL: protocolo binário, sem parsing de SQL por linha, sem round-trip por registro.
A diferença em relação a `INSERT` linha a linha é de ordem de grandeza, não percentual. E o
EF Core adiciona change tracking, materialização de entidades e geração de SQL — tudo
desperdício num caminho em que não há lógica de domínio por registro.

**Por que tabela de staging `UNLOGGED`.** `UNLOGGED` não escreve WAL, o que acelera muito a
carga. É seguro porque o dado é descartável: se o servidor cair no meio, o job é
reexecutado do zero. A staging também permite validar e transformar em SQL conjunto — com
o dado já no banco — antes de tocar a tabela real, e torna o `MERGE` uma operação
relacional em vez de meio milhão de round-trips.

**Por que uma dimensão de concorrência e não três mecanismos.** `Parallel.ForEachAsync`,
`Channel` e `SemaphoreSlim` resolvem o mesmo problema de formas diferentes; usar os três
significa três limites independentes que interagem de forma imprevisível. O `Channel`
sozinho dá pipeline com backpressure, que é o que o problema pede. O limite real é o pool
de conexões — qualquer concorrência acima disso só enfileira.

**Por que evento por lote.** Um evento por linha geraria 500 mil mensagens, cada uma
disparando invalidação de cache e reindexação. O broker aguenta; os consumidores e o
OpenSearch, não.

**Por que retomável e com rejeição por linha.** Import de 500 mil registros leva minutos e
vai falhar em produção — arquivo malformado, deploy no meio, rede. Tudo-ou-nada significa
recomeçar do zero, e uma única linha ruim descarta 499.999 boas. Nenhum dos dois é
aceitável operacionalmente.

## Alternativas descartadas

- **EF Core com `AddRange` em lotes.** Rejeitada: change tracking e geração de SQL por
  linha. Ordens de grandeza mais lento.
- **`INSERT ... ON CONFLICT` em lote, sem staging.** Melhor que EF, mas perde a validação
  em conjunto e o `MERGE` relacional. Viável para arquivos pequenos.
- **EFCore.BulkExtensions.** Boa biblioteca, mas abstrai o COPY sem oferecer controle sobre
  staging e `MERGE` — que é onde está a validação de negócio deste fluxo.
- **Quebrar o arquivo em N mensagens no broker e processar em paralelo** (o desenho
  original). Rejeitada: transforma um problema de I/O sequencial em coordenação
  distribuída, com o mesmo teto de escrita no banco no fim. Mais partes móveis, mesmo
  throughput.

## Consequências

**Positivas**
- Throughput limitado pelo banco, não pela aplicação.
- Job retomável e com relatório de rejeição por linha.
- Backpressure natural: memória constante independentemente do tamanho do arquivo.

**Negativas**
- **COPY exige SQL e Npgsql direto** — o código de import não se parece com o resto do
  sistema (que usa EF Core). É uma inconsistência deliberada e precisa estar documentada,
  senão alguém "padroniza" para EF e derruba a performance.
- Tabela de staging precisa ser limpa. Job abortado deixa resíduo.
- `MERGE` em lotes grandes pode gerar contenção com a leitura de catálogo. Lotes pequenos
  o bastante e fora do pico.
- A validação está em dois lugares: por linha no pipeline (formato, tipo) e em conjunto no
  SQL (duplicata, integridade referencial). Precisa estar claro qual regra vive onde.
