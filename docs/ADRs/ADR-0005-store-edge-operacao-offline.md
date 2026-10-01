# ADR-0005 — Store Edge: a loja vende com o link caído

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Premissa P1: a loja precisa continuar vendendo com o link caído.

A arquitetura original pressupunha que o POS alcança a API na cloud a cada venda. Em
varejo físico brasileiro isso é inaceitável: link cai por horas, e uma loja parada é
prejuízo direto e imediato — além de ser o cenário em que a operação perde confiança no
sistema de forma permanente.

Operação offline não é uma funcionalidade que se adiciona depois. Ela determina onde mora
a numeração fiscal, onde mora o certificado digital, onde o preço é resolvido e como a
consistência é reconciliada. Enxertar depois é reescrever.

## Decisão

Introduzir o container **Store Edge**: um processo .NET rodando em hardware na loja
(mini PC ou servidor de retaguarda), entre os terminais de PDV e a cloud.

**O POS nunca fala com a cloud.** Fala sempre com o Edge, em `localhost` ou na LAN da loja.
O Edge decide se está online.

Responsabilidades:

| Componente | Função |
|---|---|
| Local API | Contrato único do POS, idêntico online ou offline |
| Catalog & Price Cache | Catálogo e preço resolvido, sincronizados por delta |
| Number Range Store | Faixas de numeração fiscal arrendadas da cloud |
| NFC-e Emitter + Signer | Monta, assina e transmite a nota localmente ([ADR-0006](ADR-0006-fiscal-contexto-isolado.md)) |
| Contingency Controller | Decide `tpEmis` normal vs. contingência offline |
| Local Outbox | Vendas e notas pendentes de sincronização |
| Cloud Sync Worker | Envia a outbox, puxa deltas e novas faixas |

Persistência local em **SQLite** (arquivo único, sem servidor, sem administração na loja).

**Arrendamento de numeração:** a cloud concede blocos (ex.: 1.000 números por série) e
registra a concessão. O Edge consome do bloco e pede o próximo quando atinge um limiar.
Números concedidos e não usados são inutilizados pela cloud na reconciliação.

## Por quê

**Por que um processo na loja, e não offline no próprio POS.** Concentrar a complexidade
de conectividade, cache e fiscal em um lugar por loja, em vez de replicá-la em cada
terminal. Com 5 caixas, a alternativa significaria 5 caches para invalidar, 5 faixas de
numeração para coordenar e 5 cópias do certificado. O Edge também permite que um terminal
com defeito seja substituído sem perder estado.

**Por que o POS nunca fala com a cloud.** Se o POS tivesse dois caminhos (cloud quando
online, Edge quando offline), teríamos dois códigos de venda para manter em paridade — e
eles divergiriam. Um caminho só, com o Edge decidindo, mantém o comportamento idêntico nos
dois modos. Isso também torna o modo contingência testável de verdade: basta bloquear a
saída do Edge.

**Por que SQLite.** Sem servidor, sem porta, sem backup a gerenciar pelo gerente da loja,
transacional de verdade. Um PostgreSQL em cada loja seria uma frota de bancos para operar.

**Por que arrendar faixas em vez de gerar número na hora.** A numeração fiscal é sequencial
por loja e série. Se cada Edge gerasse números livremente, duas lojas colidiriam; se
pedisse número à cloud a cada venda, não haveria emissão offline. Arrendamento resolve os
dois: o Edge tem números seus, garantidamente exclusivos, sem depender da rede.

## Segurança

O certificado A1 fica na loja. É uma concessão consciente e a maior superfície de risco
deste desenho — **a contingência offline exige assinar localmente**, e se o link caiu, um
serviço de assinatura na cloud também está inalcançável. Não há como evitar sem abrir mão
de P1.

Mitigações obrigatórias antes da primeira loja em produção:

- Certificado em repositório protegido do SO (DPAPI no Windows), nunca em arquivo solto.
- Certificado **por loja**, não um compartilhado — permite revogar uma loja isoladamente.
- Disco cifrado no hardware do Edge.
- Rotação anual acompanhando a validade do A1, com procedimento de revogação documentado
  em runbook.
- Alerta em caso de uso do certificado fora do horário de funcionamento da loja.

## Alternativas descartadas

- **Sempre online, com link redundante.** Rejeitada por P1, e porque redundância de link
  não cobre a queda da própria SEFAZ.
- **Offline em cada terminal de PDV.** Rejeitada: multiplica cache, numeração e certificado
  por terminal.
- **Fila offline só de "intenção de venda", sem emitir nota.** Rejeitada: sem cupom fiscal
  não há venda legal. Não resolve o problema.

## Consequências

**Positivas**
- A loja é autônoma. Queda de link, de cloud ou da SEFAZ não para o caixa.
- A latência percebida no caixa é de LAN, não de internet.

**Negativas**
- **Uma frota de software para versionar e atualizar.** Atualização do Edge precisa de
  rollout escalonado, rollback e telemetria por loja. É um problema operacional novo e
  permanente, comparável a manter um app móvel.
- Certificado replicado em N lojas (ver acima).
- Consistência eventual entre loja e cloud vira normal: relatórios e estoque central
  refletem lojas offline com atraso. A UI precisa mostrar isso explicitamente, e não
  fingir que o número é atual.
- Hardware na loja é responsabilidade nova: falha do Edge para a loja inteira. Exige
  procedimento de contingência de hardware.
- Divergência de catálogo/preço entre lojas durante a propagação do delta. Preço precisa
  de data de vigência, nunca "vale a partir de agora".
