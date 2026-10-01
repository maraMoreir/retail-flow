# ADR-0009 — Pricing como contexto próprio, com cache invalidado por evento

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

No desenho original, "apply discounts" era uma responsabilidade do Sales API, e
"Promotions Engine" e "Cache Invalidation" estavam no roadmap futuro.

Preço em varejo não é um campo do produto. É o resultado de uma resolução:

```
preço final = f(sku, loja, canal, cliente/convênio, quantidade,
                promoção vigente, data/hora, regra de arredondamento)
```

Uma rede tem tabela de preço por região, preço promocional com vigência, preço por
convênio, desconto por quantidade, e combinações ("leve 3 pague 2") que não são desconto
percentual. Tudo isso com data de início e fim.

É simultaneamente **o dado mais lido do sistema** (toda leitura de item no caixa) e **o
mais crítico em correção** (preço errado é prejuízo direto ou problema com o consumidor).

## Decisão

**Pricing é um bounded context próprio**, com schema próprio, não um serviço dentro de Sales.

**Modelo:** regras de preço são declarativas e versionadas por vigência; a resolução é uma
função pura sobre elas. O resultado é determinístico e reproduzível — dado o mesmo
contexto e o mesmo instante, o preço é sempre o mesmo. Isso torna a auditoria de
"por que esse item saiu por esse valor" possível.

**Cache em três camadas:**

| Camada | Conteúdo | Invalidação |
|---|---|---|
| Edge (SQLite + memória) | Preço resolvido por SKU para a loja | Delta sync; regra com vigência futura já vai no delta |
| Redis | Preço resolvido, chave `{tenant}:price:{loja}:{canal}:{sku}` | Evento `pricing.rules.changed.v1` → worker invalida |
| Postgres | Regras de origem | — |

**Invalidação por evento, nunca por TTL curto.** Alteração de regra publica
`pricing.rules.changed.v1`; um worker invalida as chaves afetadas e o Edge recebe o delta.

**Vigência é do domínio, não do cache.** Toda regra tem `valid_from` e `valid_to`. O Edge
recebe regras com vigência futura **antes** de elas valerem, e passa a aplicá-las no
horário correto sem depender de estar online naquele instante.

**Dinheiro é `decimal`**, nunca `double`. Arredondamento é regra explícita do contexto,
aplicada num único lugar.

## Por quê

**Por que contexto separado e não um serviço dentro de Sales.** Preço muda por razões
comerciais, numa cadência que não tem nada a ver com a do fluxo de venda. Quem mexe em
promoção é o time comercial; quem mexe em venda é o time de operações. Misturar os dois
significa que cada campanha promocional toca o código do caixa.

Além disso, Pricing é consumido por vários contextos: Sales (venda), Catalog (vitrine),
Reporting (margem), e futuramente e-commerce. Como sub-rotina de Sales, os outros
precisariam chamar Sales para saber um preço — o que é errado.

**Por que vigência no domínio e não invalidação no horário.** Se a promoção começasse com
uma invalidação de cache à meia-noite, uma loja offline naquele momento continuaria
vendendo pelo preço velho. Com vigência, o Edge já tem a regra e a aplica na hora certa,
offline ou não. Isso é consequência direta de P1.

**Por que invalidação por evento e não TTL.** TTL curto significa alta taxa de miss e
carga constante no Postgres para o dado mais lido do sistema. TTL longo significa vender
por preço errado durante o TTL. Nenhum dos dois é aceitável. Evento dá invalidação
precisa: só o que mudou.

**Por que a resolução tem que ser pura e determinística.** Sem isso, é impossível
responder a "por que esse cupom saiu por R$ 19,90?" — pergunta que aparece em toda
reclamação de cliente e em toda auditoria de margem. Com resolução determinística, basta
reexecutar com o contexto da venda.

## Alternativas descartadas

- **Preço como coluna em `products`.** Rejeitada: não comporta loja, canal, cliente nem
  vigência. É o modelo que todo sistema de varejo abandona no primeiro ano.
- **Desconto calculado no Sales.** Rejeitada: acopla campanha comercial ao caixa.
- **Cache só com TTL.** Rejeitada: ver acima.
- **Motor de regras genérico (ex.: engine de scripts).** Rejeitada por ora: flexível demais,
  impossível de auditar e de otimizar. Regras declarativas tipadas cobrem os casos reais.

## Consequências

**Positivas**
- Campanha promocional não toca o código do caixa.
- Preço auditável e reproduzível.
- Loja offline aplica promoção na hora certa.

**Negativas**
- **Mais complexo que um campo de preço.** Justificado, mas é custo inicial real.
- Cache em três camadas tem três oportunidades de ficar velho. Precisa de uma métrica de
  *idade máxima do preço em cada Edge* e alerta quando passar do limiar — é o sinal de que
  uma loja está servindo preço velho.
- Regras com vigência exigem relógio confiável no Edge. NTP na loja vira requisito, e
  desvio de relógio precisa de alerta.
- Resolução determinística obriga a guardar o **contexto** da venda (quais regras se
  aplicaram), não só o valor final. Mais dado por item vendido.
