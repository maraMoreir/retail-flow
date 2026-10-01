# ADR-0016 — Saga de venda com reserva por TTL e compensação explícita

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Uma venda atravessa reserva de estoque, captura de pagamento, emissão fiscal e baixa
definitiva. Cada passo pode falhar, e os passos anteriores já produziram efeito — inclusive
efeito **fora do nosso sistema**, como uma captura no adquirente.

O desenho original não dizia o que acontece quando o pagamento é aprovado e a reserva de
estoque falha. Sem resposta explícita, o comportamento emerge de detalhes de implementação
— e o resultado típico é dinheiro capturado sem venda registrada, ou estoque baixado sem
pagamento.

`Saga Pattern` estava no roadmap futuro.

## Decisão

A venda é uma **saga de orquestração** (não coreografia), com estado persistido e
compensação explícita em cada passo.

| # | Passo | Compensação |
|---|---|---|
| 1 | Resolver preço (Pricing) | — (sem efeito colateral) |
| 2 | Reservar estoque (Redis, TTL) | Liberar reserva — ou deixar o TTL expirar |
| 3 | Capturar pagamento (TEF) | **Estornar** — efeito externo, exige ação |
| 4 | Emitir NFC-e (Edge) | Cancelar dentro da janela, ou nota de devolução depois |
| 5 | Confirmar venda + movimento no ledger | Movimento de estorno no ledger |
| 6 | Publicar `SaleCompleted.v1` (outbox) | — |

**Orquestração, não coreografia.** Um `SaleSaga` com estado explícito em
`sales.saga_state`. Nada de cada serviço reagir a eventos e torcer para a sequência
emergir certa.

**Estado persistido a cada transição**, para que a saga sobreviva à morte do processo.
Um job de recuperação retoma sagas paradas além de um limite de tempo.

**Compensação é idempotente.** Pode ser executada mais de uma vez com o mesmo resultado —
porque o job de recuperação pode disparar uma compensação que já rodou.

**A reserva expira sozinha por TTL.** É a compensação preferida do passo 2: não requer
ação, não pode falhar.

**Ponto de não retorno: a emissão da NFC-e** (passo 4). Depois dela, não há mais
compensação técnica — só processo de negócio (cancelamento dentro da janela da UF, ou nota
de devolução). A saga registra esse ponto explicitamente e trata os passos seguintes como
"precisa concluir", nunca "pode reverter".

**Passos 5 e 6 são na mesma transação local** ([ADR-0003](ADR-0003-outbox-inbox.md)),
então não podem falhar parcialmente entre si.

## Por quê

**Por que orquestração e não coreografia.** Coreografia (cada serviço reage a eventos) é
elegante em fluxos de dois ou três passos, e vira impossível de depurar em seis com
compensação. A pergunta "por que essa venda ficou pendente?" precisa de uma resposta em um
lugar só. Com orquestração, é uma linha em `saga_state`. Com coreografia, é reconstruir a
sequência a partir de logs de cinco serviços.

**Por que estado persistido.** Sem ele, um pod reiniciado no meio da saga deixa pagamento
capturado e venda não registrada, sem ninguém para perceber. O estado persistido é o que
permite o job de recuperação existir.

**Por que compensação idempotente.** O job de recuperação não tem como saber com certeza
se a compensação anterior completou — o mesmo problema de estado indeterminado do
[ADR-0006](ADR-0006-fiscal-contexto-isolado.md). Se a compensação for idempotente, ele
pode simplesmente executá-la de novo. Se não for, precisa de consulta prévia a cada passo,
o que multiplica a complexidade.

**Por que marcar o ponto de não retorno.** É a diferença entre um sistema que tenta
"desfazer" uma nota fiscal emitida (impossível, e a tentativa produz inconsistência
fiscal) e um que reconhece que dali em diante o caminho é para frente. Isso tem que estar
no modelo, não no julgamento de quem escreve o código do dia.

**Por que a reserva tem TTL e não compensação ativa.** A melhor compensação é a que não
precisa ser executada. Reserva expirada libera estoque sem nenhuma ação, mesmo que todo o
resto tenha falhado catastroficamente.

## Alternativas descartadas

- **Transação distribuída (2PC) entre os passos.** Rejeitada: o adquirente e a SEFAZ não
  participam de 2PC. Impossível por definição.
- **Coreografia por eventos.** Rejeitada: indepurável com seis passos e compensação.
- **Sem saga — apenas transação local, torcendo para dar certo.** Rejeitada: é o desenho
  original, e produz dinheiro capturado sem venda.
- **Biblioteca de saga pronta (MassTransit Saga State Machine).** **Não rejeitada.** É uma
  escolha razoável e traz persistência de estado e recuperação prontos. Fica como decisão
  de implementação, não de arquitetura — mas se for adotada, adotar por inteiro, não meio
  a meio com orquestração própria.

## Consequências

**Positivas**
- Toda falha parcial tem comportamento definido, escrito, e testável.
- "Por que essa venda ficou pendente" tem resposta em uma consulta.
- Sagas travadas são detectáveis e recuperáveis.

**Negativas**
- **Complexidade real e permanente.** Cada passo novo exige pensar a compensação. É a
  parte mais difícil do sistema de manter correta.
- Estado de saga é mais uma tabela de alta rotatividade, precisando de expurgo.
- **Compensação pode falhar.** Estorno de TEF recusado pelo adquirente exige intervenção
  humana — o que significa uma fila de exceções no Admin com dono e SLA. Não dá para
  fingir que a compensação sempre funciona.
- O ponto de não retorno torna alguns erros irreversíveis por desenho. Correto, mas exige
  que a operação entenda que dali em diante o caminho é devolução, não cancelamento.
- Testar todos os caminhos de falha é caro. É o principal alvo dos
  `tests/RetailFlow.ChaosTests`.
