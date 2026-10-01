# ADR-0029 — Fronteiras internas do Store Edge

**Status:** Proposto · **Data:** 2026-09-30
**Aplica ao Edge o que o [ADR-0022](ADR-0022-grafo-de-dependencia-entre-modulos.md) definiu para a cloud**

## Contexto

O Edge concentra venda, caixa, fiscal, assinatura, contingência, réplica de catálogo,
outbox e sincronização — tudo em um processo, sobre um SQLite.

Isso é um monólito modular, e para uma caixa de PDV está **certo**. O problema não é
estar junto; é estar junto **sem fronteira**, e virar:

```
SalesService → FiscalService → SyncService → SQLite → ERP
```

com todos alcançando tudo. O Edge é onde tudo converge, o que o torna o candidato natural
a apodrecer primeiro — e é o componente que roda em 500 máquinas fora do nosso alcance.

## Decisão

### Módulos internos, um assembly cada

```
Store Edge
├── Sales            venda, itens, desconto
├── Cash             sessão de caixa, sangria, fechamento
├── Fiscal           emissão, contingência, faixa de numeração
├── Catalog          réplica de produto e preço
├── Customer         réplica mínima, sob demanda
├── Synchronization  ciclo de sync, deltas, contrato com a cloud
├── Outbox           fila local, estados, confirmação
└── Infrastructure   SQLite, HTTP, periféricos, assinatura
```

Cada módulo com `Public API` · `Domain` · `Application` · `Persistence`.

### SQLite compartilhado, mas não a leitura cruzada

O arquivo é um só — processos separados numa caixa de PDV seriam complexidade operacional
sem contrapartida. Mas **nenhum módulo lê tabela de outro**. A separação é por prefixo de
tabela, e a regra é validada por teste de arquitetura, como na cloud.

É a versão possível do "schema por módulo" do
[ADR-0001](ADR-0001-modulos-por-contexto.md): SQLite não tem schema, então o prefixo e o
teste fazem o papel.

### Conectividade mora em um lugar só

Apenas `Synchronization` sabe se há rede. `Sales`, `Cash` e `Fiscal` **nunca** consultam
estado de conexão — escrevem na `Outbox` e seguem.

A exceção deliberada é `Fiscal`, que precisa saber se a SEFAZ respondeu para decidir
`tpEmis` ([ADR-0006](ADR-0006-fiscal-contexto-isolado.md)). Mas isso é
**"a SEFAZ respondeu?"**, não **"estou online?"** — perguntas diferentes, e confundi-las
produz o bug de a loja entrar em contingência porque a *cloud* caiu, o que não tem
relação nenhuma.

### O mesmo grafo da cloud, reduzido

```
Sales ──► Catalog · Cash · Customer · Outbox
Cash  ──► Outbox
Fiscal──► Outbox
Synchronization ──► Outbox   (lê e confirma)
Todos ──► Infrastructure
```

`Synchronization` **não** referencia `Sales`, `Cash` nem `Fiscal`. Ela lê a `Outbox` e
aplica deltas via API pública de `Catalog` e `Customer`. Mesma inversão que mantém o
`ErpIntegration` limpo na cloud
([ADR-0022](ADR-0022-grafo-de-dependencia-entre-modulos.md)).

### Não vira microsserviço

Processos separados numa caixa de loja significariam orquestração, IPC, supervisão e
depuração remota de vários processos — em hardware modesto, sem operador técnico no local.
Módulos num processo, com fronteiras de compilação, entregam o que importa (isolamento de
mudança) sem nada disso.

## Por quê

**Por que assembly por módulo aqui também.** Sem isso, `internal` não vale nada e a
fronteira é convenção de pasta. Numa base que roda fora do nosso alcance e é atualizada
por anéis, fronteira que depende de disciplina humana não sobrevive.

**Por que isolar conectividade em um módulo.** É o que torna o modo offline testável de
verdade: basta impedir `Synchronization` de sair. Se cada módulo checasse rede, testar
offline exigiria simular falha em oito lugares — e o comportamento divergiria entre eles.

**Por que separar "estou online" de "a SEFAZ respondeu".** São independentes: a SEFAZ pode
estar fora com a internet perfeita, e o link da loja pode cair com a SEFAZ no ar. Um
único booleano de "online" colapsa os dois e produz contingência desnecessária — ou,
pior, deixa de acioná-la quando precisa.

**Por que `Synchronization` não referencia os módulos de domínio.** Mesma razão da cloud:
é o módulo que mais muda (a cada versão de contrato) e não pode arrastar o domínio junto.

## Alternativas descartadas

- **Um único projeto, separação por pasta.** Rejeitada: `internal` vazaria e a fronteira
  seria só convenção.
- **Microsserviços dentro da loja.** Rejeitada: complexidade operacional
  desproporcional em hardware de loja.
- **Um banco SQLite por módulo.** Tentador para isolar de verdade. Rejeitada: impediria
  transação única entre venda, movimento e outbox — que é exatamente a garantia que o
  [ADR-0003](ADR-0003-outbox-inbox.md) depende. Um arquivo, com fronteira por convenção
  testada.
- **Reaproveitar os módulos da cloud com outro provider de persistência.** Rejeitada: o
  mundo do Edge é uma loja só, com modelo diferente. Compartilhamos domínio e
  `Fiscal.Core` ([ADR-0022](ADR-0022-grafo-de-dependencia-entre-modulos.md)), não
  repositórios.

## Consequências

**Positivas**
- Modo offline testável bloqueando um módulo.
- Contingência fiscal desacoplada de conectividade — decisões independentes, como no mundo real.
- O componente mais crítico do produto tem fronteira de compilação, não só boa intenção.

**Negativas**
- **Mais assemblies para carregar** num hardware modesto. Tempo de inicialização do Edge
  precisa ser medido — caixa que demora a subir é reclamação imediata.
- Um SQLite compartilhado **permite** violar a fronteira com um `JOIN`. O compilador não
  vê isso; só o teste de arquitetura vê, e ele precisa ser escrito de verdade.
- Sem transação distribuída entre módulos — mas também não se quer isso num processo só.
- A distinção entre "online" e "SEFAZ respondeu" é sutil e será confundida por quem chegar
  depois. Precisa estar nomeada explicitamente no código, não só neste ADR.
