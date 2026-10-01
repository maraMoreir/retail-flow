# ADR-0018 — Sessão de caixa obrigatória, com conferência cega no fechamento

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Pendência aberta no [documento de arquitetura](../DesignDocs/architecture-c4.md): o módulo
`Cashier` existia no nível 2 sem nível 3.

Gestão de caixa é o que transforma "o sistema registrou vendas" em "o dinheiro bate". Sem
ela não há como responder quem era o responsável pelo caixa às 14h, de onde veio a
diferença de R$ 47,00, ou se a sangria de R$ 2.000 realmente chegou ao cofre. É também o
principal controle contra fraude interna — que em varejo é a maior fonte de perda depois
da quebra.

## Decisão

### A sessão de caixa é obrigatória e é uma máquina de estados

```
  aberta ──> em_fechamento ──> fechada ──> conferida
     │                                        │
     └────────── sangria / suprimento ────────┘
```

**Toda venda referencia uma sessão de caixa aberta.** Venda sem sessão aberta é rejeitada
pelo domínio — não é validação de tela. Isso é o que garante que todo valor tem dono,
turno e terminal.

| Operação | O que registra |
|---|---|
| Abertura | Operador, terminal, fundo de troco declarado, timestamp |
| Sangria | Retirada para o cofre: valor, motivo, quem autorizou |
| Suprimento | Entrada de troco: valor, origem |
| Fechamento | Valores declarados pelo operador, **por meio de pagamento** |
| Conferência | Comparação declarado × esperado, diferença justificada |

### Conferência cega

**No fechamento, o operador declara os valores sem ver o esperado pelo sistema.** Só
depois de submeter a declaração a diferença é revelada — para ele e para o gerente.

Diferença acima de um limite configurável bloqueia a conclusão e exige justificativa de
gerente. A diferença é **sempre registrada**, nunca silenciosamente absorvida.

### Por meio de pagamento, não em valor único

Conferência é por meio: dinheiro, cada bandeira de cartão, Pix, vale. Um caixa pode fechar
com diferença zero no total e ter R$ 300 a mais em dinheiro e R$ 300 a menos em cartão —
o que é um sinal muito mais grave do que uma diferença pequena no total.

### Fecha mesmo offline

A sessão vive no Store Edge e fecha sem a cloud
([ADR-0005](ADR-0005-store-edge-operacao-offline.md)). O fechamento é sincronizado depois,
com as mesmas garantias de idempotência das vendas
([ADR-0004](ADR-0004-idempotencia-pdv.md)).

## Por quê

**Por que conferência cega.** É todo o valor do controle. Se o operador vê o esperado
antes de contar, ele digita o esperado — e a conferência vira teatro que produz sempre
diferença zero. Cega, a diferença é informação real. O custo é fricção com quem opera;
o benefício é o único número que detecta desvio sistemático.

**Por que venda exige sessão aberta.** Sem essa invariante, aparecem vendas fora de
qualquer turno, e não há como atribuir responsabilidade nem fechar o dia. Colocar a regra
no domínio, e não na tela do PDV, é o que impede que uma venda vinda da sincronização
offline ou de um script de correção contorne o controle.

**Por que por meio de pagamento.** Diferenças que se compensam no total são o padrão
clássico de desvio. Conferir só o total esconde exatamente o que se quer encontrar.

**Por que a diferença é sempre registrada.** Permitir que o sistema "ajuste" a diferença
para zero destrói o único indicador que existe. Diferença pequena e frequente num mesmo
operador é um sinal; diferença apagada não é sinal nenhum.

## Alternativas descartadas

- **Conferência aberta (mostrando o esperado).** Rejeitada: torna o controle inútil.
- **Sessão opcional, venda sem caixa.** Rejeitada: sem dono não há conferência possível.
- **Conferência só do total.** Rejeitada: esconde compensação entre meios.
- **Absorver diferença abaixo de um limite sem registrar.** Rejeitada: o limite é para
  exigir justificativa, não para apagar o dado.
- **Sessão por operador em vez de por terminal.** Rejeitada: gaveta é física e pertence ao
  terminal. Troca de operador no mesmo terminal fecha uma sessão e abre outra.

## Consequências

**Positivas**
- Todo valor tem operador, turno e terminal.
- Diferença de caixa vira métrica acompanhável por operador e por loja.
- Fechamento funciona offline, como o resto da operação da loja.

**Negativas**
- **Fricção real na operação.** Conferência cega por meio de pagamento é mais lenta que
  digitar um total. É o custo do controle e precisa ser defendido junto à operação, senão
  vira pedido de "simplificar" que esvazia o mecanismo.
- Venda exigir sessão aberta cria um modo de falha novo: caixa esquecido aberto da véspera,
  ou fechado por engano no meio do expediente. Exige fechamento automático por tempo, com
  registro de que foi automático.
- Mais estado no Edge para sincronizar e reconciliar.
- Troca de turno no meio de uma venda em aberto precisa de regra explícita — decidimos que
  a venda pertence à sessão em que foi **iniciada**.
