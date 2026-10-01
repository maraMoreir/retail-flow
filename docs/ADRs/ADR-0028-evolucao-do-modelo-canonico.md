# ADR-0028 — Evolução do modelo canônico

**Status:** Proposto · **Data:** 2026-09-30
**Complementa:** [ADR-0021](ADR-0021-erp-connector-anticorrupcao.md) · [ADR-0024](ADR-0024-integracao-erp-por-capacidade.md)

## Contexto

O [ADR-0021](ADR-0021-erp-connector-anticorrupcao.md) registrou o risco em uma frase: *"o
modelo canônico é uma aposta; se estiver errado, todo adaptador paga"*. Nomear risco não é
tratá-lo.

Com N adaptadores em produção, qualquer mudança no canônico é uma mudança coordenada em N
lugares, pertencentes a clientes diferentes, com janelas de manutenção diferentes.

A armadilha específica: **deixar o canônico crescer para acomodar cada ERP**. Cada campo
adicionado "porque o Protheus tem" empurra o modelo na direção de ser a união de todos os
ERPs — que é o oposto de anticorrupção.

## Decisão

### O canônico representa o domínio do PDV, não a união dos ERPs

Critério de admissão de campo, aplicado sem exceção:

> **O PDV precisa disso para vender, emitir documento fiscal, controlar caixa ou reportar
> o que a loja fez?**

Se a resposta for "não, mas o ERP X manda", o campo **não entra**. Se um ERP manda dado
que não usamos, o adaptador descarta.

### Versionamento com major/minor

| Mudança | Versão | Exemplo |
|---|---|---|
| Campo opcional novo | minor | `v1.1`: `Price` + `PromotionPrice` |
| Campo opcional vira obrigatório | **major** | — |
| Mudança de **semântica** ou de forma | **major** | `Price: decimal` → `Price: PriceRange` |
| Remoção | **major** | Só após uma major inteira de deprecação |

```
                    ┌── Canonical v1 ──┐
ERP ──► Adapter ────┤                  ├──► núcleo
                    └── Canonical v2 ──┘
```

Durante a transição, um adaptador pode produzir as duas versões. O núcleo consome a
corrente; a anterior é mantida enquanto houver adaptador não migrado.

### Adaptador declara a versão que fala

Como a capacidade ([ADR-0024](ADR-0024-integracao-erp-por-capacidade.md)), a versão do
canônico é declarada e resolvida **na inicialização**. Adaptador que fala uma versão não
suportada falha na subida, não no meio de um ciclo às 3h.

### Migração é do adaptador, nunca do núcleo

O núcleo nunca tem `if (canonicalVersion == 1)`. Quem traduz entre versões é um **upgrader
explícito** na borda:

```
Canonical v1 ──[V1ToV2Upgrader]──► Canonical v2 ──► núcleo
```

Aposentar a v1 é apagar o upgrader e os adaptadores que ainda a usavam.

### Campo de extensão: proibido

Sem `Dictionary<string, object> Extras`, sem `Custom1..Custom10`, sem payload JSON solto.

É por aí que o acoplamento volta: o campo vira um contrato informal entre um adaptador e
um consumidor, invisível ao compilador e ao teste, e em pouco tempo o núcleo tem
`if (extras["TipoProtheus"] == ...)`.

Se um dado é realmente necessário, ele entra no canônico como campo tipado, com nome do
**nosso** domínio — e passa pelo critério de admissão.

## Por quê

**Por que o critério de admissão é tão restritivo.** Porque a pressão é constante e sempre
local: cada campo pedido parece pequeno e tem um cliente esperando. Sem um critério
escrito, a soma dessas decisões razoáveis produz um modelo que é a união de todos os ERPs
— e aí ele não abstrai nada, só renomeia.

**Por que mudança de semântica é major mesmo mantendo o tipo.** É o caso que passa
despercebido: o campo continua `decimal`, mas agora significa preço com imposto em vez de
sem. Nada quebra, e os valores ficam errados em silêncio.

**Por que upgrader explícito e não tolerância no núcleo.** Condicional de versão no núcleo
espalha o conhecimento de compatibilidade pelo domínio e torna a aposentadoria uma caça a
`if`. Com upgrader, a compatibilidade mora num arquivo e morre com ele.

**Por que proibir campo de extensão.** É a decisão mais impopular deste ADR e a mais
importante. `Extras` é um cavalo de Troia: transforma acoplamento tipado em acoplamento
por string, que nenhuma ferramenta detecta. O trabalho que ele "economiza" volta
multiplicado na primeira refatoração.

## Alternativas descartadas

- **Canônico como união dos campos de todos os ERPs.** Rejeitada: não é abstração, é
  agregação. O modelo cresce sem limite e todo consumidor precisa saber quais campos
  valem para qual origem.
- **Um canônico por ERP.** Rejeitada: é não ter canônico.
- **Sem versionamento, só mudanças compatíveis.** Rejeitada: funciona até a primeira
  mudança semântica, que é inevitável.
- **Campo de extensão genérico.** Rejeitada — ver acima.
- **Versionar por campo (cada campo com sua validade).** Rejeitada: granularidade fina
  demais, difícil de raciocinar, e nenhuma ferramenta ajuda.

## Consequências

**Positivas**
- O núcleo não conhece versão do canônico.
- Aposentar uma versão é apagar arquivos, não caçar condicionais.
- Adaptador incompatível falha na subida.

**Negativas**
- **O critério de admissão vai gerar atrito comercial.** Vai haver cliente cujo dado é
  importante *para ele* e não passa no critério. A resposta correta é "isso é do ERP, não
  do PDV", e ela é impopular.
- Manter duas versões do canônico vivas significa upgraders para escrever e testar.
- Proibir `Extras` significa que **toda** necessidade nova vira mudança de modelo — mais
  lento que um campo livre, e essa lentidão é o ponto.
- Definir o canônico bem exige conhecer vários ERPs, e no início só conheceremos um. **A
  primeira versão vai estar errada.** Por isso o versionamento existe desde o dia 1, e a
  `v2` deve ser esperada, não tratada como fracasso.
