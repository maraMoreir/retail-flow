# ADR-0024 — Integração com ERP baseada em capacidades

**Status:** Proposto · **Data:** 2026-09-30
**Substitui a porta definida em:** [ADR-0021](ADR-0021-erp-connector-anticorrupcao.md)

## Contexto

O [ADR-0021](ADR-0021-erp-connector-anticorrupcao.md) definiu uma porta de adaptador com
sete operações fixas: `PullCatalog`, `PullPrices`, `PullStock`, `PullCustomers`,
`PushSales`, `PushReturns`, `PushCashierClosing`.

Isso assume que todo ERP faz as sete, da mesma forma. Nenhum faz:

- há ERP que devolve catálogo e preço **na mesma resposta**;
- há ERP sem delta nenhum — só dump completo;
- há ERP que **empurra** preço em vez de expor consulta;
- há ERP que não expõe estoque por loja.

Uma interface de sete métodos produz meia dúzia de `throw new NotSupportedException()` —
violação de Interface Segregation, e pior: o orquestrador não tem como saber o que vai
funcionar antes de chamar.

## Decisão

### Interfaces segregadas por capacidade

```csharp
public interface ICatalogProvider  { Task<CatalogPage> GetCatalogAsync(...); }
public interface IPriceProvider    { Task<PricePage>   GetPricesAsync(...);  }
public interface IStockProvider    { Task<StockPage>   GetStockAsync(...);   }
public interface ICustomerProvider { Task<CustomerPage> GetCustomersAsync(...); }
public interface ISalesPublisher   { Task PushSalesAsync(...); }
```

Um adaptador implementa **só o que o ERP faz**.

### Capacidade declarada, não booleana

Booleano não distingue "suporta preço" de "suporta preço incremental". O modo de
sincronização muda a estratégia inteira:

```csharp
public sealed record ErpCapabilities
{
    public CatalogCapability  Catalog  { get; init; }
    public PricingCapability  Pricing  { get; init; }
    public StockCapability    Stock    { get; init; }
    public CustomerCapability Customer { get; init; }
    public SalesCapability    Sales    { get; init; }
}

public enum SyncMode { None, FullSnapshot, Delta, Push }

public sealed record PricingCapability(
    SyncMode Mode,
    bool     BundledWithCatalog);   // chega junto do catálogo
```

Casos reais que isso cobre sem exceção:

| ERP | Catálogo | Preço | Estoque |
|---|---|---|---|
| A | Delta | Delta | Delta |
| B | Delta (preço junto) | `BundledWithCatalog` | FullSnapshot |
| C | FullSnapshot | **Push** | None |

### A capacidade é resolvida uma vez, na inicialização

**Esta é a regra que faz o modelo valer alguma coisa.** Na subida, o Sync Engine lê as
capacidades e monta um **pipeline concreto**:

```
Sync Engine (startup)
    │
    ├── Pricing.Mode == Delta        → DeltaStrategy
    ├── Pricing.Mode == FullSnapshot → FullSnapshotStrategy
    ├── Pricing.Mode == Push         → PushEndpointStrategy
    └── Pricing.Mode == None         → capacidade ausente, pipeline não existe
```

Depois disso **não há verificação de capacidade em tempo de execução**. Nenhum
`if (caps.Pricing.Delta)` em call site.

Sem essa regra, trocamos `NotSupportedException` por condicional espalhada — o mesmo
problema, em outro lugar.

### Capacidade ausente é decisão de produto, não erro

`Stock.Mode == None` não é falha. Significa que aquele cliente opera sem disponibilidade
vinda do ERP, e o PDV se comporta de acordo (vende sem checar saldo, e o ledger vira a
única fonte do que a loja movimentou). O comportamento para cada capacidade ausente é
definido e documentado, nunca improvisado.

### A direção da dependência

```
Domain
  ↑
Application
  ↑
Ports  (ICatalogProvider, IPriceProvider, …)
  ↑
ERP Adapters  (SAP, Protheus, Sankhya)
```

Nunca o inverso. O domínio não sabe que SAP existe.

## Por quê

**Por que capacidade e não configuração por cliente.** Configuração descreve o que **está
ligado**; capacidade descreve o que **é possível**. São coisas diferentes: um cliente pode
ter estoque disponível no ERP e escolher não sincronizar. Capacidade é do adaptador,
configuração é do cliente, e misturar as duas produz um `if` que ninguém entende depois.

**Por que enum de modo e não flags booleanas.** `FullSnapshot` e `Delta` são mutuamente
exclusivos e exigem estratégias completamente diferentes — uma tem marca d'água e é
retomável, a outra tem janela de manutenção e substitui tudo. Dois booleanos permitiriam
representar o estado inválido "ambos".

**Por que resolver na inicialização.** Além de evitar condicional espalhada, faz a
configuração inválida falhar **na subida**, não no meio de um ciclo de sincronização às
3h da manhã.

## Alternativas descartadas

- **Interface única com sete métodos** ([ADR-0021](ADR-0021-erp-connector-anticorrupcao.md)).
  Rejeitada: é o que este ADR corrige.
- **Interface única com métodos opcionais (default implementations).** Rejeitada: o
  comportamento padrão seria lançar ou devolver vazio, e "vazio" é indistinguível de
  "não suportado" — ambiguidade perigosa numa sincronização de catálogo.
- **Descoberta de capacidade em runtime, perguntando ao ERP.** Rejeitada: a maioria dos
  ERPs não sabe responder isso, e a resposta seria instável.
- **Um adaptador por cliente em vez de por ERP.** Rejeitada como padrão: dez clientes de
  Protheus dariam dez adaptadores quase iguais. Diferenças de cliente vão em configuração
  e De-Para; só um comportamento genuinamente diferente justifica adaptador novo.

## Consequências

**Positivas**
- Nenhum `NotSupportedException`. Adaptador implementa o que existe.
- Adicionar um ERP com capacidades inéditas não mexe nos adaptadores existentes.
- Configuração inválida falha na subida.

**Negativas**
- **Mais tipos e mais indireção** que uma interface única. É complexidade real, paga em
  troca de não ter exceções de não-suporte.
- **Cada combinação de capacidades é um caminho a testar.** A matriz cresce
  multiplicativamente; na prática só as combinações que existem em clientes reais são
  cobertas, e isso precisa ser consciente.
- O comportamento para cada capacidade ausente precisa ser **decidido e documentado** —
  senão vira improviso no primeiro cliente que não tiver estoque.
- Adaptadores que compartilham transporte (dois ERPs sobre SOAP) vão querer código comum.
  Isso é legítimo, mas é por onde o acoplamento entre adaptadores volta — exige vigilância.
