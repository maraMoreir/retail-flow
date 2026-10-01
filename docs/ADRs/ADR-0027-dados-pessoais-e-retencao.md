# ADR-0027 — Dados pessoais, retenção e ciclo de vida do SQLite

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Há dado pessoal circulando por uma frota de máquinas instaladas em loja, e não havia
nenhuma decisão escrita sobre isso. O único lugar que mencionava CPF era um comentário de
coluna no schema.

Fluxo real:

```
ERP ──clientes──► Cloud ──réplica──► Edge ──SQLite
                    ▲                  │
                    └────vendas────────┘
                    (CPF na nota)
```

Junto disso: o SQLite da loja cresce para sempre, sem política nenhuma.

## Decisão

### Classificação

| Dado | Pessoal? | Onde vive |
|---|---|---|
| Produto, preço | Não | Cloud + Edge |
| Nome, CPF, endereço do cliente | **Sim** | Réplica na cloud + Edge |
| CPF na nota fiscal | **Sim** — e com obrigação legal | XML no object storage |
| Identificação do operador | **Sim** | Cloud + Edge |
| Venda sem identificação | Não | Cloud + Edge |

### Minimização no Edge

O Edge recebe **apenas o necessário para vender**: identificador, nome e documento do
cliente. Não recebe endereço, telefone, histórico de compras nem dados de crédito, mesmo
que o ERP os exponha.

Réplica de cliente é **sob demanda**, por consulta pontual — não é dump do cadastro
inteiro para todas as lojas. Não faz sentido cada uma das 500 lojas ter a base completa
de clientes da rede.

### O conflito entre LGPD e obrigação fiscal, resolvido

Esse é o ponto que uma auditoria cobra, e ele precisa estar escrito:

| | Base legal | Retenção |
|---|---|---|
| **XML fiscal com CPF** | Obrigação legal | **5 anos. Não se apaga, não se anonimiza** |
| **Réplica operacional de cliente** | Execução de contrato / legítimo interesse | Curta. Expurgada quando não mais necessária |

São dados que se parecem e têm regimes opostos. Um pedido de eliminação alcança a réplica
operacional; **não** alcança o documento fiscal, porque obrigação legal é base legal
autônoma para retenção.

Consequência prática: nunca tratar os dois pelo mesmo caminho de expurgo.

### Proteção

- SQLite do Edge **cifrado em repouso**; disco da máquina cifrado.
- Segredos fora do banco — repositório protegido do SO
  ([ADR-0019](ADR-0019-ciclo-de-vida-certificado-a1.md), [ADR-0026](ADR-0026-identidade-do-edge.md)).
- CPF **nunca** em log, em rótulo de métrica, em URL ou em query string.
  Em telemetria, no máximo um hash com sal ([ADR-0013](ADR-0013-observabilidade-otlp.md)).
- Acesso ao dado pessoal na cloud é auditado: quem consultou, quando, por quê.

### Ciclo de vida do SQLite, por categoria

Regra de idade única ("apagar depois de 30 dias") quebra operação ou auditoria. A retenção
é por natureza do dado:

| Categoria | Política |
|---|---|
| **Operacional** (sessão de venda em aberto, cache) | Retenção curta, por idade |
| **Sincronização** (outbox) | Até **confirmação da cloud** — nunca por idade |
| **Fiscal** (documento emitido, contingência pendente) | Até transmitido e arquivado; depois conforme obrigação |
| **Auditoria** (trilha local) | Política própria, mais longa |
| **Réplica de cliente** | Expurgo agressivo: some quando a venda fecha |

**A regra mais importante:** uma mensagem de outbox nunca é apagada por ser velha. Ela
sai por **estado de negócio** — `pending → published → confirmed → purged`. Outbox velha
não é lixo; é venda que não chegou, e apagá-la por idade é perder faturamento em
silêncio, que é o pior modo de falha possível.

Outbox parada além de um limiar **gera alerta**, não expurgo.

## Por quê

**Por que réplica de cliente sob demanda e não dump.** Minimização não é só princípio
legal — é redução de superfície. Cada loja com a base completa de clientes multiplica o
impacto de um equipamento roubado por 500.

**Por que hash com sal em telemetria e não CPF cru.** Porque investigar incidente exige
correlacionar eventos do mesmo cliente, e hash permite isso sem armazenar o documento.
Sem sal, o espaço de CPFs é pequeno o bastante para reversão por força bruta.

**Por que a outbox sai por estado e não por idade.** Um expurgo por idade transforma
"venda que não sincronizou" em "venda que nunca existiu", sem rastro. A confirmação da
cloud é o único sinal legítimo de que a mensagem cumpriu seu papel.

**Por que separar rigorosamente fiscal de operacional.** Porque um pedido de eliminação
vai chegar, e a resposta tem de ser defensável: "apagamos o que podíamos e mantivemos o
que a lei obriga, com base legal identificada". Sem a separação no modelo, ou se apaga o
que não podia, ou não se apaga nada.

## Alternativas descartadas

- **Não replicar cliente no Edge; consultar a cloud sempre.** Rejeitada: quebra a venda
  identificada offline, que é P1.
- **Anonimizar CPF no documento fiscal após um tempo.** Rejeitada: ilegal. O XML é
  imutável e obrigatório por 5 anos.
- **Uma política única de retenção por idade para o SQLite.** Rejeitada: confunde outbox
  com cache e perde venda.
- **Cifrar campo a campo em vez do banco inteiro.** Rejeitada por ora: complexidade alta,
  e no Edge a ameaça é roubo do equipamento — contra isso, cifrar em repouso o banco e o
  disco cobre. Reconsiderar para a cloud, onde o modelo de ameaça é outro.

## Consequências

**Positivas**
- Pedido de eliminação tem resposta defensável, por categoria.
- Impacto de equipamento roubado limitado ao que aquela loja precisava.
- Outbox nunca perde venda por expurgo.

**Negativas**
- **Réplica sob demanda exige a cloud alcançável** no momento da primeira venda
  identificada daquele cliente naquela loja. Offline, ou a venda sai sem identificação, ou
  usa o que já estiver em cache. Isso precisa estar claro para a operação.
- Cifrar o SQLite tem custo de desempenho e adiciona gestão de chave no Edge.
- Retenção por categoria significa **vários jobs de expurgo** com regras diferentes, cada
  um podendo falhar de um jeito.
- A proibição de CPF em log é fácil de violar sem perceber. Precisa de verificação
  automatizada, não só de revisão de código.
- Auditoria de acesso a dado pessoal na cloud é mais uma tabela de alto volume, com seu
  próprio problema de retenção.
