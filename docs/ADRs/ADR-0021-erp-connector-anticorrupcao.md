# ADR-0021 — ERP Connector: camada de anticorrupção com modelo canônico

**Status:** Proposto · **Data:** 2026-09-30
**Depende de:** [ADR-0020](ADR-0020-escopo-pdv-multi-erp.md)

## Contexto

Definido o escopo ([ADR-0020](ADR-0020-escopo-pdv-multi-erp.md)): o RetailFlow é um PDV
que consome catálogo, preço e estoque do ERP do cliente, e devolve vendas, devoluções,
movimentos e fechamento de caixa. Sendo produto de mercado, precisa falar com **vários**
ERPs — SAP, Protheus, Sankhya e o que vier.

Cada ERP tem seu modelo de dados, seu vocabulário e seu transporte. Um expõe REST; outro,
SOAP; outro deposita arquivo em SFTP; e sempre aparece um que quer acesso direto ao banco.

O risco concreto: **o primeiro cliente define o domínio do produto.** Se o modelo do SAP
vazar para dentro do núcleo, o segundo cliente exige reescrita — e o terceiro vira um
emaranhado de `if` por cliente.

## Decisão

### Modelo canônico no núcleo; adaptador na borda

O núcleo do RetailFlow conhece **apenas** o modelo canônico. Nenhum tipo, código ou
vocabulário de ERP específico cruza essa fronteira.

```
ERP do cliente  ──►  Adapter  ──►  Canonical Model  ──►  núcleo do PDV
   (o modelo deles)    (traduz)       (o nosso)
```

### Contrato único de adaptador

Todo adaptador implementa a mesma porta. O que varia — transporte, formato, autenticação,
paginação — é absorvido dentro dele:

| Operação | Direção | Semântica |
|---|---|---|
| `PullCatalog(watermark)` | entrada | Delta de produtos desde a marca d'água |
| `PullPrices(watermark)` | entrada | Delta de preços, **com vigência** |
| `PullStock(storeId)` | entrada | Disponibilidade por loja |
| `PullCustomers(watermark)` | entrada | Delta de clientes |
| `PushSales(batch)` | saída | Lote idempotente, ordenado por loja |
| `PushReturns(batch)` | saída | Idem |
| `PushCashierClosing(batch)` | saída | Fechamento de caixa |

### Sincronização é retomável, com marca d'água por entidade

Uma marca d'água por `(cliente, ERP, entidade)`. Nunca "sincroniza tudo": um catálogo de
milhões de itens não cabe numa janela de execução. A técnica de carga é a do
[ADR-0015](ADR-0015-import-copy-staging.md) — COPY binário para staging, merge em lotes.

### De-Para explícito e versionado

Tabela de mapeamento entre o identificador do ERP e o interno, para produto, filial, meio
de pagamento e CFOP. **Nunca inferência por nome ou heurística.** Item sem De-Para é
rejeitado e vai para a fila de divergência — não é adivinhado.

### Saída idempotente e tolerante a ERP fora do ar

`PushSales` é idempotente pela chave da venda. Ordenado por loja, porque o ERP costuma
recusar movimento de período já fechado. Se o ERP está fora, o lote fica na fila — **a
venda nunca espera pelo ERP**.

### Reconciliação diária é obrigatória

Confronto do que o PDV vendeu contra o que o ERP aceitou, por loja e por dia. Divergência
gera item numa fila com dono e prazo, visível no Admin.

### Resiliência por cliente, nunca global

Circuit breaker e limite de taxa por cliente. O ERP de um cliente fora do ar não pode
afetar a sincronização dos outros.

## Por quê

**Por que anticorrupção e não integração direta.** É a diferença entre um produto e uma
customização. Sem a camada, o modelo do primeiro ERP se espalha pelo domínio e cada
cliente novo multiplica os condicionais. Com ela, um cliente novo é um adaptador novo —
trabalho conhecido, isolado e testável sozinho.

**Por que o contrato é o mesmo para todos, mesmo com transportes tão diferentes.** Porque
a diferença entre REST e arquivo em SFTP é de transporte, não de significado. "Dê-me os
produtos que mudaram desde X" é a mesma pergunta nos dois casos. Deixar a diferença vazar
para o orquestrador faria o agendamento e a retomada terem um caminho por ERP.

**Por que De-Para explícito e nunca inferência.** Casar produto por descrição ou por
similaridade de código funciona nos testes e falha em produção, silenciosamente, vendendo
o item errado. Falhar com "não há mapeamento" é muito melhor que acertar 98% e errar 2%
sem avisar.

**Por que a reconciliação não é opcional.** O ERP **vai** recusar registros: produto
inexistente, filial errada, período contábil fechado, imposto divergente. Sem confronto
diário, a divergência aparece no fechamento contábil do mês, quando já são milhares de
registros e ninguém lembra do contexto.

**Por que a venda nunca espera pelo ERP.** Mesma lógica de R1 aplicada a R2: um terceiro
lento e instável não pode estar no caminho síncrono do caixa. O ERP é destino final, não
etapa de validação.

## Alternativas descartadas

- **Integração direta, sem modelo canônico.** Rejeitada: o primeiro cliente definiria o
  domínio.
- **Um ETL genérico configurável por tela.** Tentador comercialmente, rejeitado: vira uma
  linguagem de programação mal feita, impossível de versionar, testar e depurar. Adaptador
  em código, versionado, é mais honesto.
- **Acesso direto ao banco do ERP.** Rejeitada como padrão: acopla ao schema interno de
  terceiro, que muda sem aviso em qualquer atualização. Aceitável apenas como leitura,
  num adaptador isolado, quando o cliente não oferecer outra opção — e registrado como
  dívida.
- **Sincronização completa periódica, sem marca d'água.** Rejeitada: não escala e não é
  retomável.
- **Middleware de integração de mercado (iPaaS).** Não rejeitada por mérito; reduziria
  trabalho. Rejeitada porque a lógica de reconciliação e De-Para é específica do domínio
  e acabaria espalhada entre o iPaaS e o produto.

## Consequências

**Positivas**
- Cliente novo é um adaptador novo, isolado e testável sozinho.
- O núcleo permanece limpo do vocabulário de qualquer ERP.
- Divergência tem processo, dono e prazo em vez de virar surpresa contábil.

**Negativas**
- **O modelo canônico é uma aposta.** Se estiver errado, todo adaptador paga. Vai precisar
  evoluir, e evoluir modelo canônico com N adaptadores em produção é caro.
- **Tradução tem custo de fidelidade.** Sempre haverá campo do ERP que não cabe no
  canônico. A tentação de adicionar um `extras` genérico precisa ser resistida — é por
  onde o acoplamento volta.
- **Testar é difícil**: o sistema do outro lado não é controlável nem reproduzível. Exige
  simulador por ERP nos testes de integração e testes de contrato
  (`tests/RetailFlow.ContractTests`) por adaptador.
- Cada adaptador é mais código para manter, e ERPs mudam sem aviso. Precisa de alerta de
  quebra de contrato, não só de erro em produção.
- A fila de divergência precisa de gente olhando. Sem dono nomeado, vira um depósito de
  registros que ninguém trata.
