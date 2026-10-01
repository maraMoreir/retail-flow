# ADR-0002 — Quatro deployables na cloud; extrair sob pressão, não por antecipação

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

O desenho original previa seis serviços independentes na cloud desde o início: Sales,
Inventory, Fiscal, Notification, Reporting e Worker. O sistema ainda não tem tráfego, nem
time definido, nem perfil de carga medido.

## Decisão

Começar com **quatro processos** na cloud, mais um na loja:

| Deployable | Conteúdo | Motivo de existir separado |
|---|---|---|
| `RetailFlow.Api` | Catalog, Pricing, Sales, Inventory, Customer, Cashier, Payments | Caminho síncrono. Escala por RPS. |
| `RetailFlow.Fiscal` | NF-e, faixas, guarda, reconciliação | Depende de terceiro lento com limite de taxa. Isolamento de falha. |
| `RetailFlow.Workers` | Projeções, notificação, fidelidade, auditoria | Escala por profundidade de fila, não por RPS. |
| `RetailFlow.OutboxRelay` | Publicação da outbox | Padrão de acesso ao banco e cardinalidade próprios. |
| `RetailFlow.BatchImport` | Carga em massa | Isolamento de recurso ([ADR-0011](ADR-0011-pgbouncer-isolamento-pools.md)). |
| `RetailFlow.StoreEdge` | Roda na loja | [ADR-0005](ADR-0005-store-edge-operacao-offline.md). |

Critério objetivo para extrair um módulo depois: **perfil de escala diferente**, **perfil de
falha diferente** ou **cadência de release conflitante**. Não "porque é microsserviço".

## Por quê

Seis serviços sem tráfego não é arquitetura distribuída — é monolito distribuído. Sales
chamando Inventory por HTTP paga latência de rede, serialização, timeout, retry,
inconsistência parcial e rastreamento distribuído para resolver um problema que ainda não
existe. E como os dois seriam versionados e implantados juntos de qualquer forma, não há
nem o benefício de autonomia de release.

O custo de não separar agora é baixo porque [ADR-0001](ADR-0001-modulos-por-contexto.md)
já mantém a fronteira: módulos com schema próprio e comunicação por interface pública. A
extração é trocar uma chamada in-process por um cliente HTTP atrás da mesma interface.

O custo de separar cedo é alto e permanente: cada fronteira de processo vira um contrato
que precisa de versionamento, teste de contrato e compatibilidade retroativa.

**Fiscal é a exceção justificada.** É o único cujo tempo de resposta depende de um sistema
fora do nosso controle, com limite de taxa e indisponibilidade frequente. Dentro da Api,
uma parada da SEFAZ prenderia threads e conexões de banco que a venda precisa — falha de um
terceiro derrubando o caixa. A separação aqui compra isolamento de falha real, hoje.

## Alternativas descartadas

- **Monolito único (tudo num processo).** Rejeitada: workers de fila e API síncrona têm
  modelos de escala incompatíveis; e Fiscal precisa de isolamento de falha.
- **Seis serviços desde o dia 0.** Rejeitada pelos motivos acima.
- **Serverless por função.** Rejeitada: cold start no caminho do caixa e dificuldade de
  manter pool de conexões contra PostgreSQL.

## Consequências

**Positivas**
- Menos superfície operacional para depurar durante o desenvolvimento.
- Transação local ainda disponível dentro da Api enquanto os módulos convivem — mas o
  código não deve assumi-la (ver consequências do ADR-0001).

**Negativas**
- A Api concentra sete módulos: um bug de memória em Pricing derruba Sales junto. Aceito
  enquanto o volume não justificar o contrário.
- Escala é grosseira: escalar Sales significa escalar Catalog junto, desperdiçando recurso.
  É o primeiro sintoma que deve disparar a extração.
- Exige disciplina contínua. Sem os testes de arquitetura, os módulos viram um monolito
  comum em poucos meses.
