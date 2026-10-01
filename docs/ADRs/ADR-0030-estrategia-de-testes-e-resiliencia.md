# ADR-0030 — Estratégia de testes e resiliência

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

`ContractTests` e `ChaosTests` aparecem como nomes de pasta em vários ADRs, sem nunca
terem sido definidos. Num sistema cujos dois maiores riscos são **comportamento offline**
e **integração com terceiro não controlável**, o que se testa e onde não é detalhe de
implementação — é arquitetura.

Os dois riscos têm a mesma característica: **não se reproduzem em teste unitário e não se
observam em produção antes de doerem.**

## Decisão

### A pirâmide, com os dois eixos que o produto exige

```
                    E2E
                  /     \
          Integration   Chaos
              |           |
           Contract    Offline
               \         /
          Unit / Domain / Application
```

`Contract` e `Offline` não são variações de integração — são categorias próprias, porque
respondem a perguntas que nenhuma outra camada responde.

### Domain — rápido, sem infraestrutura

Invariantes que, se quebrarem, produzem prejuízo ou problema fiscal:

- cálculo de venda, desconto, arredondamento;
- devolução parcial e devolução sobre devolução ([ADR-0017](ADR-0017-devolucao-troca-cancelamento.md));
- abertura, sangria e fechamento de caixa, com conferência cega ([ADR-0018](ADR-0018-sessao-de-caixa.md));
- escolha de `tpEmis` e histerese da contingência ([ADR-0006](ADR-0006-fiscal-contexto-isolado.md));
- elegibilidade de cancelamento pela janela da UF;
- numeração sequencial e inutilização.

### Contract — um por adaptador de ERP, com respostas gravadas

```
resposta real gravada (golden file)
        │
        ▼
    Adapter
        │
        ▼
 Canonical Model  ──► comparado com o esperado
```

**Respostas reais gravadas**, não fixtures inventadas — não dá para chamar o SAP do
cliente em CI, e resposta inventada testa o que imaginamos, não o que o ERP manda.

Cobre também: campo ausente, campo a mais, acentuação, formato de número e data, página
vazia, resposta parcial.

Segundo eixo: **contrato Edge ↔ Cloud** ([ADR-0023](ADR-0023-contrato-versionado-edge-cloud.md)).
Para cada versão suportada, um teste que prova que um cliente daquela versão continua
funcionando. É o que impede quebra silenciosa de compatibilidade.

### Offline — derrubando a rede de verdade

Não `IsOnline => false`. Bloqueio real no nível de rede, com o Edge rodando:

```
rede OFF ──► venda ──► NFC-e em contingência ──► SQLite ──► outbox
                                                              │
rede ON  ──────────────────────────────────────► sync ────────┘
                                                              │
                                              reconciliação ──┘
```

Cenários obrigatórios: venda offline completa com emissão em contingência; transmissão do
lote ao voltar; sincronização com a cloud sem duplicar
([ADR-0004](ADR-0004-idempotencia-pdv.md)); fechamento de caixa offline; rede voltando
**no meio** de uma venda; SEFAZ fora **com** internet disponível — que é caso distinto e
frequentemente confundido ([ADR-0029](ADR-0029-fronteiras-internas-do-store-edge.md)).

### Chaos — a lista concreta

"Simulamos indisponibilidade" não é teste. A lista:

| Falha | Alvo |
|---|---|
| Cloud indisponível | Edge continua vendendo |
| ERP indisponível | Venda não espera; lote acumula |
| SEFAZ indisponível / `cStat 108`, `109` | Contingência aciona com histerese |
| Timeout de rede | Distinguido de erro — dispara consulta, não reemissão |
| Mensagem duplicada | Inbox descarta ([ADR-0003](ADR-0003-outbox-inbox.md)) |
| Mensagem fora de ordem | Consumidor comutativo ou versão do agregado |
| Resposta parcial / truncada | Não é tratada como sucesso |
| Banco travado (`SQLITE_BUSY`) | Retry sem corromper |
| Processo morto no meio da transação | Saga retomada ([ADR-0016](ADR-0016-saga-venda-compensacao.md)) |
| Disco quase cheio na loja | Degrada de forma previsível, avisa antes |
| Relógio desajustado | Vigência de preço e prazo fiscal não quebram em silêncio |
| Falha de health check pós-migração | Rollback com snapshot ([ADR-0025](ADR-0025-atualizacao-frota-edge.md)) |

### Integration — através do PgBouncer

Rodam contra PostgreSQL real em Testcontainers e **através do PgBouncer**, não direto no
banco. É a única forma de pegar as restrições do transaction pooling — estado de sessão,
prepared statements — que funcionam em desenvolvimento e falham intermitentemente em
produção ([ADR-0011](ADR-0011-pgbouncer-isolamento-pools.md)).

### Architecture — a fronteira como build

Valida o grafo de [ADR-0022](ADR-0022-grafo-de-dependencia-entre-modulos.md) e
[ADR-0029](ADR-0029-fronteiras-internas-do-store-edge.md): ausência de ciclo, dependências
permitidas, e — no Edge, onde o compilador não vê — **nenhuma query cruzando prefixo de
tabela de outro módulo**.

## Por quê

**Por que offline é categoria própria.** Porque é o produto. Um mock de `IsOnline`
testa o `if`, não o comportamento — não pega socket pendurado, timeout parcial, conexão
que cai no meio de um POST, nem DNS que resolve mas não conecta. E são esses que
acontecem em loja.

**Por que respostas gravadas nos testes de contrato.** Fixture inventada codifica nossa
suposição sobre o ERP. O bug real é sempre o campo que vem diferente do que imaginamos —
e uma fixture escrita por nós nunca o contém.

**Por que testar "SEFAZ fora com internet no ar" separadamente.** É a confusão mais
provável no código: tratar os dois como "offline". Se o Edge entrar em contingência
porque a *cloud* caiu, estará emitindo em contingência sem necessidade — com prazo de
transmissão correndo e risco fiscal desnecessário.

**Por que integração através do PgBouncer.** Porque a diferença entre conexão direta e
transaction pooling não aparece em teste, só em produção sob carga, e de forma
intermitente — o pior tipo de bug para diagnosticar.

## Alternativas descartadas

- **Cobertura percentual como meta.** Rejeitada: incentiva testar getter e ignorar
  contingência fiscal. A meta é cobrir os cenários listados, não uma porcentagem.
- **Mockar a rede para os testes offline.** Rejeitada — ver acima.
- **Chamar o ERP de homologação do cliente em CI.** Rejeitada: indisponível, lento, e
  torna o build dependente de terceiro. Vale como suíte separada, agendada, fora do
  caminho do build.
- **E2E como principal rede de proteção.** Rejeitada: lento, instável e diagnóstico ruim.
  E2E cobre poucos caminhos felizes; o risco real está em contrato, offline e chaos.

## Consequências

**Positivas**
- Os dois maiores riscos têm suíte própria, com cenários nomeados.
- Quebra de compatibilidade de contrato falha no build, não no cliente.
- Fronteira de módulo é build, não revisão.

**Negativas**
- **Chaos e offline são caros de construir e lentos de rodar.** Não cabem no build de cada
  commit — vão para suíte noturna, o que significa feedback em horas, não em minutos.
- Respostas gravadas **envelhecem**: o ERP muda e o golden file continua passando. Precisa
  de revalidação periódica contra o ambiente real do cliente, que é trabalho recorrente e
  o primeiro a ser abandonado.
- Testcontainers com PostgreSQL **e** PgBouncer deixa o teste de integração
  significativamente mais lento.
- A matriz de versões de contrato multiplica os testes de contrato por versão suportada.
- Testar contingência exige um simulador de SEFAZ controlável — mais um componente para
  construir e manter fiel.
