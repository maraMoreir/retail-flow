# ADR-0020 — RetailFlow é um PDV multi-ERP, não uma plataforma de varejo

**Status:** Proposto · **Data:** 2026-09-30
**Reenquadra:** ADR-0001 a ADR-0019

## Contexto

O README descrevia *"enterprise retail platform inspired by real-world Point of Sale (POS)
and ERP systems"*. Isso foi lido como "a plataforma faz as duas coisas", e os ADRs 0001 a
0019 foram escritos sob essa premissa — com Catalog, Pricing, Inventory e Reporting como
contextos **donos** dos seus dados.

O escopo real é outro: **o RetailFlow é um PDV que se integra ao ERP do cliente**, e é
vendido como produto de mercado, logo precisa falar com vários ERPs diferentes.

Isso não é um detalhe. Muda quem é dono de qual dado, e dono de dado é o que define
bounded context.

## Decisão

### O ERP é dono; o PDV replica e reporta

| Dado | Dono | Papel do RetailFlow |
|---|---|---|
| Catálogo de produtos | **ERP** | Réplica local, somente leitura |
| Preço | **ERP** | Réplica com vigência, somente leitura |
| Estoque (saldo) | **ERP** | Consome disponibilidade; **reporta** movimento |
| Clientes | **ERP** | Réplica |
| **Venda** | **RetailFlow** | Fonte da verdade |
| **Documento fiscal** | **RetailFlow** | Fonte da verdade |
| **Sessão de caixa** | **RetailFlow** | Fonte da verdade |
| **Pagamento / TEF** | **RetailFlow** | Fonte da verdade |

Regra que resolve as dúvidas de fronteira: **o RetailFlow é dono do que acontece no
balcão.** Cadastro, política comercial e contabilidade são do ERP.

### O ErpConnector é contexto de primeira classe

Deixa de ser uma seta no canto do diagrama e passa a ser o componente mais complexo do
sistema, com camada de anticorrupção e adaptador por ERP.
Desenho em [ADR-0021](ADR-0021-erp-connector-anticorrupcao.md).

### Impacto nos ADRs existentes

**Sobrevivem intactos** — são sobre a operação da loja, que é o núcleo do produto:

| ADR | Por que continua valendo |
|---|---|
| [0003](ADR-0003-outbox-inbox.md) Outbox/Inbox | Ainda há dual-write; agora também contra o ERP |
| [0004](ADR-0004-idempotencia-pdv.md) Idempotência | Inalterado, e mais importante ainda |
| [0005](ADR-0005-store-edge-operacao-offline.md) Store Edge | O diferencial do produto |
| [0006](ADR-0006-fiscal-contexto-isolado.md) Fiscal partido | Inalterado |
| [0011](ADR-0011-pgbouncer-isolamento-pools.md) PgBouncer | Inalterado |
| [0013](ADR-0013-observabilidade-otlp.md) Observabilidade | Inalterado |
| [0016](ADR-0016-saga-venda-compensacao.md) Saga | Inalterado |
| [0017](ADR-0017-devolucao-troca-cancelamento.md) Devolução | Inalterado |
| [0018](ADR-0018-sessao-de-caixa.md) Caixa | Inalterado |
| [0019](ADR-0019-ciclo-de-vida-certificado-a1.md) Certificado A1 | Inalterado |

**Encolhem:**

- **[0001](ADR-0001-modulos-por-contexto.md)** — a lista de módulos muda. Saem `Catalog`,
  `Pricing` e `Inventory` como contextos donos; entram `StoreCatalog` (réplica),
  `ErpIntegration` e `Cashier`. A regra de schema por módulo continua.
- **[0007](ADR-0007-estoque-ledger-append-only.md)** — o ledger **continua**, mas muda de
  papel: não é mais a fonte da verdade do saldo, é o **registro do que a loja movimentou**,
  que será reportado ao ERP. A justificativa de contenção some (não há mais `UPDATE` de
  saldo disputado); a de auditabilidade e reconciliação fica, e fica mais forte.
- **[0009](ADR-0009-pricing-contexto-cache.md)** — o motor de resolução sai. Fica apenas a
  **réplica com vigência** e o cache. A parte sobre vigência ser do domínio, e não do
  cache, continua essencial para a loja offline.
- **[0010](ADR-0010-particionamento-retencao.md)** — o volume de catálogo deixa de existir.
  Continua valendo para venda, itens, documento fiscal e movimento — que é onde o volume
  sempre esteve.
- **[0015](ADR-0015-import-copy-staging.md)** — deixa de ser "importação de fornecedor" e
  vira **sincronização ERP→PDV**. A técnica (COPY binário, staging, merge, retomável) é a
  mesma; o gatilho e o dono mudam.

**Saem:**

- **[0012](ADR-0012-opensearch-busca-catalogo.md)** — busca facetada de catálogo é problema
  do ERP ou do e-commerce, não do PDV. O caixa busca por código de barras e por descrição
  curta, e PostgreSQL resolve. **Substituído** pela decisão de não ter motor de busca.
- **Contexto de Reporting** — é o ERP que consolida. O RetailFlow expõe os dados da
  operação de loja e a reconciliação, nada além.
- **[0014](ADR-0014-tenantid-dormente.md)** — muda de natureza. Sendo produto de mercado,
  multi-tenant deixa de ser hipótese e vira caminho provável. A decisão de manter
  `TenantId` dormente **continua certa**, mas o inventário de prontidão daquele ADR passa
  a ser roteiro de trabalho, não exercício preventivo.

## Por quê

**Por que isso melhora o projeto.** Tentar ser PDV e dono de catálogo/preço/estoque ao
mesmo tempo é competir com o ERP do cliente — que já tem esses dados, já tem os processos
em volta deles, e não vai abrir mão. O produto com fronteira nítida é mais fácil de vender
e muito mais fácil de construir.

**Por que o ledger sobrevive mesmo sem ser dono do saldo.** Porque é o registro do que a
loja fez, e é ele que alimenta a reconciliação contra o ERP. Sem ele, quando o ERP recusar
um movimento não há como saber o que o PDV afirmou ter feito.

**Por que a réplica de preço continua exigindo vigência.** É consequência de P1: a loja
offline precisa aplicar a promoção na hora certa sem depender de estar conectada. A regra
tem de chegar **antes** de valer. Isso é verdade independentemente de quem é o dono do
preço.

## Alternativas descartadas

- **PDV + retaguarda de loja** (RetailFlow dono de catálogo, preço promocional e estoque
  físico; ERP só para financeiro). Rejeitada pelo escopo definido, mas é um caminho
  legítimo de evolução se os clientes pedirem promoção local.
- **Integração direta com um ERP específico.** Rejeitada: é produto de mercado.
- **Não replicar nada, consultar o ERP em tempo real.** Rejeitada por P1 e por R2: o ERP
  cai, e é lento demais para o caminho do caixa.

## Consequências

**Positivas**
- Fronteira de produto nítida: "o que acontece no balcão".
- Muito menos superfície para construir e manter.
- O diferencial (offline + fiscal) fica evidente, em vez de diluído.

**Negativas**
- **A complexidade não sumiu, mudou de lugar.** Saiu do domínio e entrou na integração —
  e integração multi-ERP é mais difícil de testar, porque o sistema do outro lado não é
  controlável nem reproduzível.
- **Cada cliente novo é um projeto de integração**, não um cadastro. Isso é modelo de
  negócio, não só arquitetura.
- Dependência de disponibilidade e qualidade de dado do ERP do cliente. Catálogo ruim no
  ERP vira PDV ruim, e a culpa percebida é do PDV.
- Os documentos escritos antes deste ADR precisam ser lidos com este reenquadramento em
  mente. O diagrama atualizado é
  [../../retail-flow.drawio](../../retail-flow.drawio).
