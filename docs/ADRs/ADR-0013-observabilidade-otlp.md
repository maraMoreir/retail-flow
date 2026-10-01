# ADR-0013 — Telemetria única via OTLP; Seq apenas em desenvolvimento

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

O stack original listava, simultaneamente: OpenTelemetry, Prometheus, Grafana e Seq.

Na prática isso significa três interfaces distintas durante um incidente: Seq para logs,
Prometheus/Grafana para métricas, e um terceiro lugar para traces. Correlacionar um erro
entre as três é manual — copiar um `traceId` de uma tela e colar em outra, na madrugada.
É exatamente quando a fricção custa caro.

## Decisão

**Um caminho de saída: OTLP.** Todo serviço — incluindo o Store Edge — emite logs,
métricas e traces via OpenTelemetry para um **OTel Collector**, que roteia:

```
Serviços ──OTLP──> OTel Collector ──┬──> Tempo    (traces)
                                    ├──> Loki     (logs)
                                    └──> Mimir    (métricas)
                                              ↓
                                          Grafana
```

**Seq fica só no `docker-compose.dev.yml`.** Continua sendo a melhor experiência de log
estruturado em .NET durante o desenvolvimento local; não vai para produção.

### Contexto obrigatório em toda telemetria

Sem isso, telemetria de sistema distribuído é inútil:

| Campo | Por quê |
|---|---|
| `trace_id` / `span_id` | W3C Trace Context, propagado **inclusive através do RabbitMQ** |
| `store.id` | Todo incidente de varejo começa com "que loja?" |
| `terminal.id` | Isola um caixa com defeito |
| `service.name`, `service.version` | Identifica a versão em rollout escalonado |
| `sale.id` / `fiscal.key` | Correlaciona ao fato de negócio |

**Propagação pelo broker** é o ponto crítico: a outbox grava `trace_parent`
([ADR-0003](ADR-0003-outbox-inbox.md)) e o consumidor restaura o contexto. Sem isso, o
trace quebra exatamente na fronteira assíncrona — que é onde ele mais importa.

### Store Edge

Emite em **buffer local**, envia quando online. Telemetria de loja offline é justamente a
que mais interessa depois, e não pode ser perdida por falta de conectividade no momento
do evento.

### Sinais que importam

**SLI de negócio, não só de infra:**

- Tempo do fechamento de venda no PDV (p50/p95/p99) — o SLI que o operador sente.
- Taxa de vendas emitidas em contingência, por loja.
- Lojas com sincronização atrasada há mais de N minutos.
- Idade máxima do preço em cada Edge ([ADR-0009](ADR-0009-pricing-contexto-cache.md)).
- Profundidade da DLQ por fila ([ADR-0008](ADR-0008-rabbitmq-sem-kafka.md)).
- Lag do snapshot de estoque ([ADR-0007](ADR-0007-estoque-ledger-append-only.md)).
- Documentos fiscais em estado indeterminado
  ([ADR-0006](ADR-0006-fiscal-contexto-isolado.md)).

**Amostragem:** traces com *tail sampling* no Collector — 100% de erros e de requisições
lentas, taxa baixa para o caminho feliz. Volume de trace em varejo é alto demais para
guardar tudo, e o caminho feliz é o menos interessante.

## Por quê

**Por que um só destino.** Durante um incidente, a pergunta é "o que aconteceu com a venda
X na loja Y às 14h32". Com backends separados, isso são três buscas manuais correlacionadas
por copiar e colar. Com Grafana sobre Tempo/Loki/Mimir, é um clique do trace para os logs
daquele span. A economia não é de licença — é de minutos por incidente, quando eles valem
mais.

**Por que OTLP como único protocolo de saída.** Desacopla a aplicação do backend. Trocar
Loki por outra coisa é mudar configuração do Collector, não código de N serviços. É a
principal razão de existir do Collector.

**Por que Seq continua em desenvolvimento.** A experiência de consulta de log estruturado
em .NET é genuinamente melhor que a do Loki, e em desenvolvimento não há necessidade de
correlacionar com traces distribuídos. Não é inconsistência — é ferramenta certa para cada
contexto.

**Por que SLI de negócio.** CPU e memória não dizem se a loja consegue vender. "Tempo de
fechamento de venda no p99" e "lojas em contingência" dizem. O alerta que acorda alguém
deve ser sobre o segundo grupo.

## Alternativas descartadas

- **Manter os quatro (Seq em produção também).** Rejeitada: três UIs por incidente.
- **Application Insights / Datadog / New Relic.** Não rejeitadas por mérito — reduzem
  bastante a operação. Rejeitadas por custo em volume de varejo (cobrança por ingestão
  cresce com o número de lojas) e por acoplamento. Como tudo sai por OTLP, migrar depois
  é mudança de configuração.
- **Logs em arquivo + ELK.** Rejeitada: mais peças, e não resolve correlação com traces.
- **Amostragem head-based.** Rejeitada: decide antes de saber se a requisição falhou, então
  perde justamente os traces de erro.

## Consequências

**Positivas**
- Um clique do trace ao log. Correlação é estrutural, não manual.
- Trocar backend é configuração do Collector.
- SLIs expressos em termos que o negócio entende.

**Negativas**
- **O Collector é ponto único no caminho da telemetria.** Precisa de HA e de buffer — se
  cair, perde-se visibilidade justamente durante um incidente.
- Grafana + Tempo + Loki + Mimir é mais infraestrutura para operar do que um Seq. O ganho
  é de correlação, e é preciso reconhecer o custo.
- Loki tem modelo de consulta diferente de Seq (baseado em rótulos): cardinalidade alta em
  rótulo é um antipadrão. `store.id` como rótulo é aceitável; `sale.id` **não** — tem que
  ser conteúdo estruturado da linha, não rótulo.
- Buffer de telemetria no Edge consome disco na loja e precisa de limite com descarte.
