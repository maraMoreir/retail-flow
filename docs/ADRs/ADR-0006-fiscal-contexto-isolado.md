# ADR-0006 — Fiscal partido em dois: NFC-e no Edge, NF-e e governança na cloud

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

O desenho original tratava o fiscal como "mais um consumidor RabbitMQ": a venda publica um
evento, um worker genérico gera a nota. Isso ignora três características da emissão fiscal:

1. **A NFC-e precisa estar autorizada antes de imprimir o cupom.** O DANFE carrega o
   protocolo de autorização. Não dá para imprimir e autorizar depois.
2. **A numeração é sequencial por loja e série**, e buracos exigem um ato formal
   (inutilização). Não é um `bigserial`.
3. **A SEFAZ cai.** E quando cai, a loja não pode parar — existe um modo de operação
   previsto em lei (contingência offline), que é diferente de "tentar de novo".

Somado à premissa P1 (venda offline), emitir NFC-e a partir da cloud é inviável: se o link
caiu, a cloud está inalcançável — e um serviço de assinatura remoto também estaria.

## Decisão

Partir o contexto fiscal em dois deployables, por **quem está esperando**.

### No Store Edge — NFC-e (cliente no balcão)

- Monta e **assina localmente** com o certificado A1 da loja.
- Transmite à SEFAZ com **timeout curto** (alvo: 5s, configurável por UF).
- **Contingency Controller** decide o modo:
  - Resposta com `cStat 100` → imprime DANFE normal.
  - `cStat 108` (serviço paralisado momentaneamente), `cStat 109` (paralisado sem
    previsão), erro de rede ou estouro do timeout → reemite com `tpEmis=9` (contingência
    offline), imprime DANFE em contingência e enfileira na outbox local.
- Transmite o lote de contingência quando o serviço volta, dentro do prazo da UF.
- A decisão tem **histerese**: entra em contingência após N falhas consecutivas e só volta
  ao normal após uma consulta de status bem-sucedida. Não oscila por requisição.

### Na cloud — NF-e e governança

| Componente | Responsabilidade |
|---|---|
| Number Range Allocator | Arrenda faixas por loja e série; registra o que foi concedido |
| NF-e Builder | Entrada, transferência, devolução, venda B2B — ninguém esperando |
| Status Monitor | Consulta disponibilidade da SEFAZ por UF, alimenta a decisão do Edge |
| Reconciliation Job | Resolve documentos em estado indeterminado |
| Inutilização Service | Queima formalmente os números concedidos e não usados |
| XML Archiver | Guarda o XML autorizado por 5 anos em object storage, sob `{tenant}/{cnpj}/{ano}/{mes}/{chave}.xml` |

**Circuit breaker por UF, não global.** SEFAZ do Paraná fora não pode afetar a emissão em
São Paulo.

**Prazos são configuração por UF.** Janela de cancelamento da NFC-e e prazo de transmissão
de contingência variam por estado e mudam. Nunca constantes no código.

## Por quê

**Por que o Reconciliation Job é obrigatório.** Quando um envio dá timeout, não sabemos se
a SEFAZ autorizou. Reenviar cegamente produz duplicidade (`cStat 204` — duplicidade de
NF-e; `cStat 539` — duplicidade com diferença na chave). O caminho correto é **consultar a
chave de acesso antes de reemitir**:

- autorizada → recupera o protocolo e arquiva;
- inexistente → reemite com segurança;
- duplicidade → recupera o protocolo do documento já existente.

Sem esse job, toda instabilidade de rede vira uma nota duplicada e um problema fiscal que
alguém vai descobrir na apuração do mês seguinte.

**Por que a inutilização é um serviço e não um script.** Números concedidos ao Edge e não
usados (bloco parcialmente consumido antes de uma troca de série, hardware substituído,
loja fechada) precisam ser formalmente queimados. Se não forem, a numeração tem buracos
inexplicados na fiscalização.

**Por que o XML vai para object storage e não para o banco.** São 5 anos de retenção.
Guardar XML em coluna `text` infla a tabela, destrói a eficiência do cache do Postgres e
torna backup e restore lentos. O banco guarda a chave de acesso, o protocolo, o status e a
URL do objeto.

**Por que Fiscal é deployable separado na cloud.** Ver
[ADR-0002](ADR-0002-quatro-deployables.md): é o único componente cujo tempo de resposta
depende de um terceiro com limite de taxa e indisponibilidade frequente.

## Alternativas descartadas

- **Toda emissão na cloud.** Rejeitada por P1 e pela latência no balcão.
- **Toda emissão no Edge.** Rejeitada: NF-e de entrada, transferência e devolução não
  nascem na loja, e a governança (reconciliação, inutilização, guarda) precisa de visão
  da rede inteira.
- **Fiscal como worker genérico no pool comum.** Rejeitada: uma parada da SEFAZ consumiria
  todo o pool de workers e pararia notificação, projeções e auditoria junto.
- **Terceirizar para um provedor de emissão (SaaS fiscal).** Não rejeitada — é uma opção
  legítima que remove muita complexidade. Mas ainda exigiria Edge e contingência, porque o
  provedor também fica inalcançável com o link caído. Deve ser reavaliada como decisão de
  build-vs-buy separada.

## Consequências

**Positivas**
- O caixa não depende da cloud nem da SEFAZ para concluir uma venda.
- Falha da SEFAZ em uma UF fica contida naquela UF.
- Estado fiscal indeterminado tem um dono e um processo de resolução.

**Negativas**
- Certificado A1 replicado nas lojas (mitigações em
  [ADR-0005](ADR-0005-store-edge-operacao-offline.md#segurança)).
- Lógica de emissão existe em dois lugares. O montador de XML e o assinador devem ser
  **uma biblioteca compartilhada**, não duas implementações — senão divergem.
- Testar contingência exige simular a SEFAZ. Precisa de um fake controlável nos
  `IntegrationTests` e de um cenário nos `ChaosTests`.
- O estado "emitida em contingência, ainda não transmitida" é visível ao negócio e precisa
  aparecer no Admin com prazo restante, não ficar escondido.
