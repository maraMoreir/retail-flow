# ADR-0011 — PgBouncer em transaction pooling e isolamento de pools por workload

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Cada instância da aplicação .NET mantém seu próprio pool de conexões Npgsql. Num cenário
de escala horizontal:

```
30 pods de Api × pool máx. 100  = 3.000 conexões
10 pods de Workers × 50         =   500
 4 pods de BatchImport × 50     =   200
                                  -------
                                   3.700 conexões
```

O `max_connections` default do PostgreSQL é 100. Mesmo elevado a 500, cada conexão custa
um processo de backend e memória de trabalho. O banco cai por esgotamento de conexão muito
antes de a CPU chegar perto do limite.

Somado a isso: uma importação de 500 mil produtos consumindo o mesmo pool que o caixa
significa que a carga em massa degrada a venda — inaceitável.

## Decisão

### PgBouncer em modo `transaction`

Todos os serviços conectam via PgBouncer, nunca direto no PostgreSQL.

```ini
pool_mode = transaction
default_pool_size = 25          ; por par (usuário, banco)
max_client_conn = 5000
```

**Cada workload usa um usuário de banco distinto**, o que cria pools independentes no
PgBouncer:

| Workload | Usuário | `pool_size` | Destino |
|---|---|---|---|
| Api (escrita) | `rf_api` | 40 | primary |
| Api (leitura) | `rf_api_ro` | 30 | réplica |
| Fiscal | `rf_fiscal` | 10 | primary |
| Workers | `rf_worker` | 20 | primary |
| OutboxRelay | `rf_relay` | 6 | primary |
| **BatchImport** | `rf_batch` | **8** | primary |
| Reporting/Admin | `rf_report` | 10 | réplica |

O teto baixo do `rf_batch` é intencional: é o mecanismo que impede a importação em massa
de consumir a capacidade de que o caixa precisa.

### Ajustes obrigatórios no Npgsql

Transaction pooling não garante a mesma conexão física entre comandos. Consequências:

- **Prepared statements**: PgBouncer suporta a partir da 1.21, com
  `max_prepared_statements` configurado. Em versão anterior, é obrigatório
  `Max Auto Prepare=0` na connection string.
- **Nada de estado de sessão**: sem `SET` de sessão, sem `LISTEN/NOTIFY`, sem tabela
  temporária fora da transação, sem advisory lock de sessão (só o de transação).
- Pool do Npgsql pequeno (`Maximum Pool Size` ~10 por pod): quem faz o pooling de verdade
  é o PgBouncer.

## Por quê

**Por que transaction pooling e não session pooling.** Em session pooling a conexão do
servidor fica presa ao cliente enquanto ele estiver conectado — o que não reduz nada. O
ganho de multiplexação só existe em transaction pooling, onde a conexão volta ao pool no
fim de cada transação.

**Por que usuários distintos por workload.** É o mecanismo mais simples de isolamento no
PgBouncer: o pool é por par (usuário, banco). Sem isso, todos competem pelo mesmo pool e o
BatchImport pode monopolizá-lo. Bônus: permissões diferenciadas por usuário — `rf_report`
só precisa de `SELECT`, e isso reduz o raio de um incidente.

**Por que isolamento de recurso e não só fila separada.** Fila separada
([ADR-0008](ADR-0008-rabbitmq-sem-kafka.md)) impede que o import ocupe os workers de venda,
mas não impede que ele ocupe as *conexões de banco*. Os dois mecanismos são necessários:
fila separada, pool de workers separado **e** pool de conexões separado.

**Por que as restrições do Npgsql importam.** São a fonte clássica de bug ao adotar
PgBouncer: código que funciona em desenvolvimento (conexão direta) quebra de forma
intermitente em produção. Precisam ser regra explícita e verificadas nos
`IntegrationTests`, que devem rodar **através do PgBouncer**, não direto no banco.

## Alternativas descartadas

- **Elevar `max_connections` no PostgreSQL.** Rejeitada: cada conexão é um processo com
  memória própria; milhares de backends degradam o banco mesmo ociosos.
- **Pool interno da aplicação apenas.** Rejeitada: não há coordenação entre pods.
- **Pgpool-II.** Rejeitada: faz muito mais que pooling (replicação, balanceamento, cache de
  query), com superfície operacional bem maior. PgBouncer faz uma coisa bem.
- **Proxy gerenciado da nuvem (RDS Proxy, Azure connection pooling).** Não rejeitada — se o
  banco for gerenciado, é uma alternativa legítima e com menos operação. Decidir junto com
  a escolha de provedor.

## Consequências

**Positivas**
- Milhares de clientes sobre dezenas de conexões reais.
- Isolamento de recurso por workload, com um dial explícito por pool.
- Permissões granulares por usuário reduzem o raio de incidentes.

**Negativas**
- **Mais um componente no caminho de todo acesso a dados.** PgBouncer fora = sistema fora.
  Exige alta disponibilidade (múltiplas instâncias atrás de um VIP ou service do K8s).
- **Restrições reais no que o código pode fazer** (sem estado de sessão, cuidado com
  prepared statements). Precisa ser conhecimento compartilhado do time, não descoberta
  individual em produção.
- Depuração fica indireta: `pg_stat_activity` mostra o usuário do pool, não o pod de
  origem. Mitigar com `application_name` por serviço.
- Saturação de pool vira uma nova classe de incidente. Exige métrica de tempo de espera
  em fila do PgBouncer, com alerta.
