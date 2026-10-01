# ADR-0025 — Atualização da frota de Store Edges

**Status:** Proposto · **Data:** 2026-09-30
**Relacionado:** [ADR-0023](ADR-0023-contrato-versionado-edge-cloud.md) — define a janela de suporte do contrato

## Contexto

O [ADR-0005](ADR-0005-store-edge-operacao-offline.md) reconheceu que o Edge é "uma frota
de software para versionar, comparável a manter um app móvel" — e parou aí, como
consequência negativa.

Para um produto em que cada loja tem uma caixa rodando software que **emite documento
fiscal**, a estratégia de atualização é decisão arquitetural de primeira classe. Uma
atualização ruim distribuída para 500 lojas de uma vez é um incidente de faturamento.

A mudança conceitual: **o Edge não é uma aplicação desktop, é um dispositivo gerenciado.**

## Decisão

### Inventário de dispositivos na cloud

Cada instalação é um registro:

| Campo | Uso |
|---|---|
| `DeviceId` | Identidade do dispositivo ([ADR-0026](ADR-0026-identidade-do-edge.md)) |
| `StoreId` | Loja onde está instalado |
| `CurrentVersion` / `TargetVersion` | Estado e destino |
| `ContractVersion` | Casado com [ADR-0023](ADR-0023-contrato-versionado-edge-cloud.md) |
| `Ring` | Anel de implantação |
| `LastSeen` | Detecta loja sumida |
| `UpdateStatus` | `idle` · `pending` · `downloading` · `staged` · `failed` · `rolled_back` |

### Anéis de implantação

| Anel | Alcance | Permanência mínima |
|---|---|---|
| 0 | Ambiente interno | 1 dia |
| 1 | Lojas piloto (acordadas com o cliente) | 3 dias |
| 2 | ~10% da frota | 3 dias |
| 3 | Restante | — |

A promoção entre anéis é **decisão da cloud**, não do Edge. O Edge nunca escolhe versão.

### Fluxo de atualização

```
Cloud publica alvo
      │
      ▼
Edge: verifica assinatura do pacote
      │
      ├─► baixa  ──► confere checksum
      │
      ├─► instala em staging (não substitui a instalação ativa)
      │
      ├─► health check
      │      ├── ok      ──► ativa
      │      └── falha   ──► rollback, reporta, permanece na versão anterior
      │
      └─► reporta estado final
```

Requisitos do pacote: **assinado**, checksum verificado, instalação **idempotente**
(reexecutar não corrompe), estado da atualização **persistido** (sobrevive a queda de
energia no meio), **downgrade bloqueado** exceto por comando explícito da cloud.

### Migração de schema do SQLite: forward-only

**Rollback de binário não desfaz migração de banco.** Se a v5.4 migrou o SQLite e o health
check falha, voltar para v5.3 deixa um arquivo que a v5.3 não entende.

Decisão: **migrações do Edge são forward-only**, e antes de qualquer migração o Edge tira
**snapshot do arquivo SQLite**. O rollback restaura binário **e** snapshot, juntos.

Consequência dura: o snapshot é anterior à migração, então **vendas concluídas entre o
snapshot e a falha seriam perdidas** no rollback. Por isso a janela de atualização é
obrigatória (abaixo) e o rollback só é permitido se não houve venda desde o snapshot;
caso contrário o Edge fica em estado `failed` e escala para intervenção humana. Perder
venda é pior que ficar parado.

### Janela de atualização configurável

Nunca durante o expediente. Janela por loja, respeitando fuso e horário de funcionamento.
Atualizar um caixa às 18h de sábado é incidente autoinfligido.

### Offline não bloqueia nada

Loja offline simplesmente não atualiza — fica `pending` e aplica quando conseguir. E, o
mais importante: **a atualização nunca é pré-requisito para vender**. Venda, fiscal, caixa
e persistência continuam funcionando na versão instalada, indefinidamente.

O limite é a janela de suporte do contrato
([ADR-0023](ADR-0023-contrato-versionado-edge-cloud.md)): passando dela, o Edge continua
vendendo offline, mas só consegue falar com o canal de recuperação até atualizar.

### A janela de suporte sai daqui

```
janela de suporte do contrato ≥ (permanência somada dos anéis)
                               + (período máximo plausível de loja offline)
                               + margem
```

Mudar a cadência de anéis **obriga** a revisar [ADR-0023](ADR-0023-contrato-versionado-edge-cloud.md).

## Por quê

**Por que a cloud decide e não o Edge.** Edge que busca "a versão mais nova" transforma
qualquer publicação acidental em implantação total imediata. Com alvo definido pela cloud,
publicar e implantar são ações separadas.

**Por que anéis e não porcentagem aleatória.** Anel 1 são lojas **acordadas com o
cliente**, que sabem que são piloto e reportam. Porcentagem aleatória distribui o risco
para quem não foi avisado, e o problema chega como reclamação em vez de relatório.

**Por que staging + health check em vez de substituir direto.** Porque a falha que
importa não é o download — é o software subir e não conseguir emitir nota. Health check
tem de exercitar o caminho crítico, incluindo assinatura de um documento de teste, antes
de ativar.

**Por que snapshot em vez de migração reversível.** Migração reversível parece mais
elegante, mas escrever e testar o `down` de cada migração é trabalho recorrente que quase
sempre é feito mal e nunca é exercitado. Snapshot de um arquivo SQLite é uma cópia — mais
grosseiro e muito mais confiável.

## Alternativas descartadas

- **Atualização manual por técnico.** Rejeitada: não escala, e a frota fica em versões
  arbitrárias para sempre.
- **Auto-update sem anéis.** Rejeitada: uma regressão atinge a rede inteira de uma vez.
- **Container com orquestrador na loja (k3s).** Rejeitada: complexidade operacional
  desproporcional para uma caixa por loja, e exige quem saiba operar isso remotamente.
- **Migrações reversíveis no SQLite.** Rejeitada em favor de snapshot — ver acima.
- **Bloquear a venda até atualizar.** Rejeitada categoricamente: contraria P1.

## Consequências

**Positivas**
- Regressão fica contida no anel onde apareceu.
- Estado da frota é visível: quem está em que versão, quem sumiu, quem falhou.
- Atualização respeita o horário da loja.

**Negativas**
- **É um subsistema inteiro para construir**: publicação, assinatura, distribuição,
  inventário, health check, rollback, telemetria de frota. Comparável a manter um app
  móvel, e precisa estar pronto antes da segunda loja.
- **Rollout completo leva ~uma semana** pelos anéis. Correção urgente de bug fiscal precisa
  de um caminho expresso — que é exatamente o caminho mais perigoso e precisa de aprovação
  explícita.
- Snapshot do SQLite consome disco na loja e precisa de política de limpeza.
- O caso "falhou após venda desde o snapshot" **não tem solução automática** e vai gerar
  chamado. É o preço de não perder venda.
- Anel 1 depende de lojas dispostas a ser piloto — isso é negociação comercial, não
  decisão técnica.
