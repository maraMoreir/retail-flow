# ADR-0017 — Cancelamento, devolução e troca são três fluxos distintos

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Pendência aberta no [documento de arquitetura](../DesignDocs/architecture-c4.md): o ledger
de estoque e a saga de venda já previam `return`, mas o fluxo fiscal não estava modelado.

A tentação é tratar devolução como "venda com valor negativo". Isso quebra em três
lugares: o documento fiscal é outro, o prazo é outro, e a autorização é outra. Pior,
contamina os relatórios — faturamento passa a ser uma soma com sinal, e a margem fica
irrecuperável.

Há três operações que o varejo trata como uma só e que são diferentes:

| Operação | Quando | Documento fiscal |
|---|---|---|
| **Cancelamento** | Dentro da janela da UF, nota ainda "fresca" | Evento de cancelamento sobre a nota original |
| **Devolução** | Depois da janela | **Nova** nota de devolução (entrada) |
| **Troca** | Qualquer momento | Devolução + nova venda |

## Decisão

### Três agregados, não um

**Cancelamento** é um evento sobre o documento fiscal existente, não um agregado novo.
Vive em Fiscal. Só é oferecido se a janela da UF ainda estiver aberta — o Admin e o PDV
consultam a elegibilidade, nunca assumem.

**Devolução (`Return`) é um agregado próprio** no contexto Sales, com ciclo de vida,
autorização e documento fiscal próprios. Referencia a venda original e os itens
devolvidos, que podem ser um subconjunto. Gera:

- movimento `return` no ledger, quantidade positiva ([ADR-0007](ADR-0007-estoque-ledger-append-only.md));
- nota fiscal de devolução emitida pela loja;
- estorno no meio de pagamento, quando aplicável.

**Troca** não é um tipo próprio. É uma `Return` e uma `Sale` novas, ligadas por um
`exchange_id` comum, criadas na mesma saga. Se a nova venda falhar, a devolução é
compensada ([ADR-0016](ADR-0016-saga-venda-compensacao.md)).

### Regras que moram no domínio, não na tela

- **Devolução parcial é o caso normal**, não a exceção. O modelo é por item e quantidade.
- **Devolução sobre devolução é proibida.** A soma do que já foi devolvido por item é
  invariante do agregado, verificada na escrita — não no front.
- **Prazo de devolução é política comercial**, configurável, distinta do prazo fiscal.
- **Motivo é obrigatório e tipado** (defeito, arrependimento, divergência, garantia),
  porque alimenta indicador de qualidade e acerto com fornecedor.
- **Devolução de item com defeito não volta para o estoque vendável.** O movimento vai
  para uma localização de quarentena, não para o saldo disponível. Tratar como estoque
  normal é como o sistema passa a mentir sobre disponibilidade.

### Nunca estorno automático

Devolução exige autorização explícita de gerente acima de um limite configurável. O
estorno no adquirente é acionado como passo compensável da saga, com o estado
"aguardando estorno" visível no Admin caso o adquirente recuse.

## Por quê

**Por que agregado separado e não venda negativa.** Faturamento, ticket médio, margem e
comissão são calculados sobre vendas. Se devolução for uma venda de valor negativo, ou
todo relatório passa a filtrar por sinal (e alguém vai esquecer), ou os números ficam
errados de um jeito difícil de perceber. Separar mantém "venda" com um significado só.

**Por que a troca é composição e não um terceiro tipo.** Um tipo `Exchange` duplicaria
toda a lógica de devolução e toda a de venda, com duas oportunidades de divergir. Como
composição, herda automaticamente qualquer regra nova das duas pontas.

**Por que a quarentena importa.** É a diferença entre saber e não saber o que se tem para
vender. Item devolvido com defeito somado ao saldo disponível produz venda de produto
inexistente — e o erro só aparece no balcão, com o cliente na frente.

## Alternativas descartadas

- **Venda com valor negativo.** Rejeitada: contamina todos os indicadores.
- **Devolução como estado da venda original** (`sale.status = 'returned'`). Rejeitada: não
  comporta devolução parcial nem múltiplas devoluções sobre a mesma venda.
- **Tipo `Exchange` próprio.** Rejeitada: duplicação.
- **Cancelamento e devolução no mesmo fluxo, decidindo por data.** Rejeitada: escondem
  documentos fiscais e autorizações diferentes atrás de um `if`.

## Consequências

**Positivas**
- Faturamento continua sendo uma soma de valores positivos.
- Devolução parcial e múltipla funcionam sem caso especial.
- Motivo tipado vira indicador de qualidade sem trabalho extra.

**Negativas**
- Mais um agregado, mais uma saga, mais um tipo de documento fiscal.
- Relatórios de resultado líquido precisam compor duas fontes explicitamente — é mais
  trabalho, mas é trabalho visível, em vez de um sinal esquecido.
- Quarentena exige o conceito de **localização** dentro da loja, que o modelo de estoque
  ainda não tinha. Amplia [ADR-0007](ADR-0007-estoque-ledger-append-only.md).
- Devolução emitida em contingência herda toda a complexidade fiscal do
  [ADR-0006](ADR-0006-fiscal-contexto-isolado.md).
