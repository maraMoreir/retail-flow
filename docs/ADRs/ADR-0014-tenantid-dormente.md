# ADR-0014 — `TenantId` presente e dormente desde o dia 0

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Premissa P3: o RetailFlow atende **uma rede**, com N lojas. Não é SaaS multi-tenant hoje.

Mas multi-tenancy é a decisão arquitetural mais cara de reverter que existe. Adicioná-la
depois exige: alterar toda tabela transacional, preencher historicamente, revisar **toda**
consulta do sistema para incluir o filtro, e garantir que nenhuma esqueceu — sendo que
esquecer significa vazar dado de uma rede para outra. É uma reescrita disfarçada de
migração.

Ao mesmo tempo, implementar isolamento de tenant completo agora (RLS, roteamento por
tenant, quota por tenant) é complexidade paga por uma necessidade que não existe e pode
nunca existir.

## Decisão

**Meio-termo deliberado: a coluna existe, o mecanismo não.**

1. **`TenantId` como value object no `SharedKernel`**, presente em todo agregado raiz e em
   toda tabela transacional.
2. **Valor único fixo**, resolvido de configuração e injetado por um `ITenantContext`.
   Nenhum lugar do código escreve o valor literal.
3. **Todo repositório filtra por `TenantId`**, via query filter global do EF Core. O filtro
   é escrito e exercitado desde o primeiro dia, mesmo sendo sempre verdadeiro.
4. **Índices compostos começam por `TenantId`** onde ele seria a primeira coluna num
   cenário multi-tenant.
5. **Sem RLS, sem roteamento por tenant, sem quota por tenant.** Esses são o trabalho da
   migração futura, se ela acontecer.

O que a migração para SaaS exigiria depois: habilitar RLS nas tabelas, resolver o tenant
a partir do token em vez da configuração, adicionar quota e rate limit por tenant no
gateway. **Nenhuma alteração de schema, nenhum backfill, nenhuma varredura de consultas.**

## Por quê

**A parte cara de multi-tenancy não é o mecanismo — é a coluna e a disciplina de filtro.**
Habilitar RLS é uma migração curta. Adicionar uma coluna a 900 milhões de linhas de
`sale_items`, preencher, reindexar e depois auditar centenas de consultas para garantir
que todas filtram corretamente — isso é meses de trabalho e risco de vazamento entre
clientes.

**O filtro precisa estar exercitado desde o começo.** Se o query filter global só for
adicionado no dia da migração, ninguém sabe quais consultas o contornam (SQL cru, views,
relatórios, jobs). Escrevendo desde já, qualquer código que o burle aparece imediatamente,
enquanto o sistema é pequeno e a correção é barata.

**A ordem do índice importa e não dá para corrigir barato.** Num índice
`(tenant_id, store_id, created_at)`, a ordem das colunas define quais consultas ele
atende. Recriar índices em tabelas de centenas de milhões de linhas em produção é operação
longa. Definir a ordem agora custa nada.

**Por que não implementar RLS já.** RLS tem custo de planejamento de query, exige
configurar o tenant por conexão — o que interage mal com transaction pooling
([ADR-0011](ADR-0011-pgbouncer-isolamento-pools.md)) — e adiciona uma classe de bug sutil.
Pagar isso por um tenant só é desperdício.

**Custo real desta decisão: 16 bytes por linha** e uma cláusula `WHERE` sempre verdadeira.

## Prontidão: o que já está pronto e o que não está

Auditoria do desenho atual contra o que a migração exigiria. A regra de corte é simples:
**o que é caro depois tem que estar pronto agora; o que é configuração pode esperar.**

### Pronto — nada a fazer na migração

| Item | Onde | Por que já está resolvido |
|---|---|---|
| `TenantId` em toda tabela transacional | [ADR-0001](ADR-0001-modulos-por-contexto.md) | Sem `ALTER TABLE`, sem backfill |
| Ordem dos índices compostos | [ADR-0010](ADR-0010-particionamento-retencao.md) | Recriar índice em tabela de 900M linhas é operação longa |
| Query filter global exercitado | [ADR-0001](ADR-0001-modulos-por-contexto.md) | Consultas que o burlam aparecem hoje, não no dia da migração |
| `tenant_id` na outbox e na inbox | [ADR-0003](ADR-0003-outbox-inbox.md) | Permite leitura com justiça entre tenants |
| PK da idempotência composta | [ADR-0004](ADR-0004-idempotencia-pdv.md) | A chave vem do cliente — sem isso é vazamento entre tenants |
| Prefixo de tenant nas chaves Redis | [ADR-0007](ADR-0007-estoque-ledger-append-only.md) · [ADR-0009](ADR-0009-pricing-contexto-cache.md) | Reescrever esquema de chaves invalidaria todo o cache quente |
| Caminho do XML no object storage | [ADR-0006](ADR-0006-fiscal-contexto-isolado.md) | Reorganizar 5 anos de XML depois é migração de petabytes |
| Alias por tenant no OpenSearch | [ADR-0012](ADR-0012-opensearch-busca-catalogo.md) | Sem RLS no motor, a convenção de alias é a única barreira — e ela falha fechada |
| Schema único compartilhado | este ADR | Migração de schema continua sendo uma só, para todos os tenants |

### Não está pronto — é o trabalho real da migração

| # | O que falta | Custo | Por que foi adiado |
|---|---|---|---|
| 1 | Habilitar RLS e escrever as políticas | Médio | Custo de planejamento de query sem benefício com um tenant |
| 2 | Resolver tenant do token em vez da config | Baixo | `ITenantContext` já isola isso num ponto |
| 3 | Quota e rate limit por tenant no gateway | Baixo | Configuração |
| 4 | Topologia de fila por tenant ou com justiça | **Médio-alto** | Ver abaixo |
| 5 | Provisionamento e onboarding de tenant | Alto | É produto, não arquitetura |

### As duas armadilhas que precisam estar decididas antes, não depois

**RLS interage mal com transaction pooling — e precisa falhar fechado.**
A política de RLS lê o tenant de uma variável de sessão. Com PgBouncer em modo transação
([ADR-0011](ADR-0011-pgbouncer-isolamento-pools.md)), `SET` não sobrevive entre comandos;
o que funciona é `SET LOCAL` **dentro** da transação, cujo escopo é a própria transação.
Isso obriga duas coisas:

- Toda transação precisa emitir o `SET LOCAL`. Uma que esqueça vê o tenant como nulo — e
  a política **tem que negar tudo nesse caso**, nunca liberar. Política que falha aberta
  transforma um esquecimento em vazamento silencioso.
- O usuário da aplicação **não pode ser dono da tabela nem superusuário**, porque o dono
  ignora RLS por padrão. Exige `ALTER TABLE ... FORCE ROW LEVEL SECURITY` e uma separação
  entre o papel que faz migração e o papel que roda a aplicação. Isso é decisão de
  provisionamento do banco e é melhor nascer certa do que ser corrigida depois.

**A estratégia de partição pode precisar mudar.**
Hoje o particionamento é temporal ([ADR-0010](ADR-0010-particionamento-retencao.md)), que
é o certo para um tenant. Com muitos tenants de tamanhos muito diferentes, um tenant
grande e um pequeno dividem a mesma partição mensal, e a manutenção de um afeta o outro.
A mitigação é que **particionamento é definido por partição**: dá para subparticionar por
`hash(tenant_id)` apenas nos meses futuros, deixando os antigos como estão. Não é
migração de dados, é mudança na criação das próximas partições — desde que `tenant_id`
já esteja na chave, que é justamente o que este ADR garante.

## Alternativas descartadas

- **Ignorar multi-tenancy completamente.** Rejeitada: transforma uma eventual migração em
  reescrita, e a barreira para virar produto passa a ser arquitetural, não comercial.
- **Multi-tenancy completo agora (RLS + roteamento + quota).** Rejeitada por P3:
  complexidade sem necessidade correspondente.
- **Schema por tenant.** Rejeitada mesmo como futuro: com milhares de tabelas particionadas
  por schema, migração de schema vira um problema combinatório. Discriminador + RLS escala
  melhor para muitos tenants pequenos, que é o perfil provável de um SaaS de varejo.
- **Banco por tenant.** Adequado para poucos tenants grandes; custo operacional alto.
  Reavaliar só se surgir um cliente que exija isolamento físico por contrato.

## Consequências

**Positivas**
- Virar SaaS é migração de mecanismo, não de dados.
- O hábito de filtrar por tenant já está no código e nos testes.
- Índices já têm a ordem correta.

**Negativas**
- **Código carrega um conceito que não é usado**, o que confunde quem chega. Mitigar com
  um comentário no value object apontando para este ADR — sem isso, alguém vai "limpar"
  o campo em nome da simplicidade.
- 16 bytes por linha e uma coluna a mais em vários índices. Mensurável em centenas de
  milhões de linhas, mas pequeno.
- **Risco de falsa sensação de segurança**: a coluna existir não impede vazamento entre
  tenants. O isolamento real só existe com RLS. Enquanto houver um tenant só isso é
  inofensivo, mas precisa estar claro que a migração ainda exige trabalho de segurança
  de verdade.
