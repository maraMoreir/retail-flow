-- =============================================================================
-- RetailFlow — Schema inicial (v1)
-- PostgreSQL 17
--
-- Documento de referência, não uma migração. As migrações reais serão geradas
-- a partir daqui, por módulo.
--
-- Decisões materializadas neste arquivo:
--   ADR-0001  um schema por módulo; nenhum JOIN entre contextos
--   ADR-0003  outbox por módulo, inbox por consumidor
--   ADR-0004  idempotência com PK composta (tenant_id, key)
--   ADR-0006  fiscal: faixas arrendadas, estado indeterminado, XML fora do banco
--   ADR-0007  estoque como ledger append-only + snapshot
--   ADR-0009  preço com vigência, nunca "vale a partir de agora"
--   ADR-0010  particionamento temporal mensal; alta rotatividade em partição diária
--   ADR-0014  tenant_id em tudo, primeira coluna dos índices compostos; RLS preparado
--   ADR-0017  Return como agregado próprio; troca como composição
--   ADR-0018  sessão de caixa obrigatória; conferência por meio de pagamento
--
-- CONVENÇÕES
--   * Toda tabela transacional tem tenant_id como PRIMEIRA coluna da PK e dos
--     índices compostos. Hoje há um tenant só (ADR-0014).
--   * Tabela particionada tem a coluna de partição na PK — consequência do
--     particionamento declarativo, documentada no ADR-0010.
--   * Filhos herdam o created_at do pai para cair na MESMA partição e permitir FK.
--
-- TIPOS MONETÁRIOS (alinhados ao layout da NF-e)
--   numeric(21,10)  preço unitário  — vUnCom aceita até 10 casas
--   numeric(15,2)   valores         — vProd, vNF
--   numeric(15,4)   quantidade      — qCom aceita até 4 casas
--   NUNCA float/double.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 0. Extensões
-- -----------------------------------------------------------------------------
create extension if not exists pgcrypto;   -- gen_random_uuid()

-- pg_partman entra na seção 11 (requer a extensão instalada no servidor).

-- -----------------------------------------------------------------------------
-- 1. Schemas — um por bounded context (ADR-0001)
-- -----------------------------------------------------------------------------
create schema if not exists shared;
create schema if not exists catalog;
create schema if not exists pricing;
create schema if not exists inventory;
create schema if not exists sales;
create schema if not exists fiscal;
create schema if not exists payments;
create schema if not exists cashier;
create schema if not exists staging;

comment on schema shared  is 'Kernel compartilhado. Sem regra de negócio de nenhum contexto.';
comment on schema staging is 'Área de carga em massa. Conteúdo descartável (ADR-0015).';

-- =============================================================================
-- 2. SHARED
-- =============================================================================

create table shared.tenants (
    id          uuid        primary key default gen_random_uuid(),
    name        text        not null,
    document    varchar(14) not null,               -- CNPJ do grupo
    created_at  timestamptz not null default now(),
    constraint tenants_document_uk unique (document)
);
comment on table shared.tenants is
    'Hoje uma linha só. A tabela existe para que virar SaaS seja migração e não reescrita (ADR-0014).';

create table shared.stores (
    tenant_id     uuid        not null references shared.tenants (id),
    id            uuid        not null default gen_random_uuid(),
    code          text        not null,             -- código operacional, ex. '001'
    name          text        not null,
    document      varchar(14) not null,             -- CNPJ da filial: define o certificado A1 (ADR-0019)
    state_code    char(2)     not null,             -- UF: define prazos fiscais (ADR-0006)
    is_active     boolean     not null default true,
    created_at    timestamptz not null default now(),
    primary key (tenant_id, id),
    constraint stores_code_uk unique (tenant_id, code)
);
create index stores_document_ix on shared.stores (tenant_id, document);

create table shared.terminals (
    tenant_id   uuid        not null,
    id          uuid        not null default gen_random_uuid(),
    store_id    uuid        not null,
    code        text        not null,               -- número do caixa
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    primary key (tenant_id, id),
    foreign key (tenant_id, store_id) references shared.stores (tenant_id, id),
    constraint terminals_code_uk unique (tenant_id, store_id, code)
);

create table shared.operators (
    tenant_id   uuid        not null,
    id          uuid        not null default gen_random_uuid(),
    name        text        not null,
    document    varchar(11),                        -- CPF
    is_active   boolean     not null default true,
    created_at  timestamptz not null default now(),
    primary key (tenant_id, id)
);

-- Prazos fiscais por UF. Configuração, nunca constante no código (ADR-0006).
create table shared.fiscal_state_rules (
    state_code                  char(2)     primary key,
    nfce_cancel_window_minutes  int         not null,
    contingency_deadline_hours  int         not null,
    updated_at                  timestamptz not null default now()
);
comment on table shared.fiscal_state_rules is
    'Prazos variam por UF e mudam. Carregado por configuração e versionado fora do código (ADR-0006).';

-- -----------------------------------------------------------------------------
-- 2.1 Idempotência (ADR-0004)
--     PK composta com tenant_id: a chave vem do CLIENTE, não é única entre tenants.
--     Partição diária: TTL de 7 dias, expurgo por DETACH (ADR-0010).
-- -----------------------------------------------------------------------------
create table shared.idempotency_keys (
    tenant_id     uuid        not null,
    key           uuid        not null,
    endpoint      text        not null,
    request_hash  bytea       not null,
    status        text        not null default 'in_flight'
                  check (status in ('in_flight', 'completed')),
    response_code int,
    response_body jsonb,
    created_at    timestamptz not null default now(),
    expires_at    timestamptz not null,
    primary key (tenant_id, key, created_at)
) partition by range (created_at);

create index idempotency_expires_ix on shared.idempotency_keys (expires_at);

create table shared.idempotency_keys_2026_09 partition of shared.idempotency_keys
    for values from ('2026-09-01') to ('2026-10-01');
create table shared.idempotency_keys_2026_10 partition of shared.idempotency_keys
    for values from ('2026-10-01') to ('2026-11-01');

-- =============================================================================
-- 3. OUTBOX / INBOX  (ADR-0003)
--    Uma outbox POR SCHEMA: a gravação tem de estar na mesma transação do
--    agregado, e agregado não cruza schema (ADR-0001).
-- =============================================================================

create table sales.outbox_messages (
    id            bigint      generated always as identity,
    tenant_id     uuid        not null,
    occurred_at   timestamptz not null default now(),
    aggregate_id  uuid        not null,
    type          text        not null,          -- 'retail.sales.completed.v1'
    payload       jsonb       not null,
    trace_parent  text,                          -- W3C traceparent: mantém o trace através do broker
    processed_at  timestamptz,
    attempts      int         not null default 0,
    last_error    text,
    primary key (id, occurred_at)
) partition by range (occurred_at);

-- Índice parcial: o relay só enxerga o que falta publicar.
create index sales_outbox_pending_ix on sales.outbox_messages (occurred_at, id)
    where processed_at is null;

create table sales.outbox_messages_2026_09 partition of sales.outbox_messages
    for values from ('2026-09-01') to ('2026-10-01');
create table sales.outbox_messages_2026_10 partition of sales.outbox_messages
    for values from ('2026-10-01') to ('2026-11-01');

comment on column sales.outbox_messages.tenant_id is
    'Na linha, não só no payload: permite leitura com justiça entre tenants (ADR-0014).';

-- Mesma estrutura em inventory, fiscal, catalog e pricing.
create table inventory.outbox_messages (like sales.outbox_messages including all)
    partition by range (occurred_at);
create table inventory.outbox_messages_2026_09 partition of inventory.outbox_messages
    for values from ('2026-09-01') to ('2026-10-01');
create table inventory.outbox_messages_2026_10 partition of inventory.outbox_messages
    for values from ('2026-10-01') to ('2026-11-01');

create table fiscal.outbox_messages (like sales.outbox_messages including all)
    partition by range (occurred_at);
create table fiscal.outbox_messages_2026_09 partition of fiscal.outbox_messages
    for values from ('2026-09-01') to ('2026-10-01');
create table fiscal.outbox_messages_2026_10 partition of fiscal.outbox_messages
    for values from ('2026-10-01') to ('2026-11-01');

create table catalog.outbox_messages (like sales.outbox_messages including all)
    partition by range (occurred_at);
create table catalog.outbox_messages_2026_09 partition of catalog.outbox_messages
    for values from ('2026-09-01') to ('2026-10-01');
create table catalog.outbox_messages_2026_10 partition of catalog.outbox_messages
    for values from ('2026-10-01') to ('2026-11-01');

create table pricing.outbox_messages (like sales.outbox_messages including all)
    partition by range (occurred_at);
create table pricing.outbox_messages_2026_09 partition of pricing.outbox_messages
    for values from ('2026-09-01') to ('2026-10-01');
create table pricing.outbox_messages_2026_10 partition of pricing.outbox_messages
    for values from ('2026-10-01') to ('2026-11-01');

-- Inbox: message_id é gerado pelo NOSSO publicador, então é único por construção.
-- Diferente da chave de idempotência (ADR-0003/0004).
create table inventory.inbox_messages (
    message_id   uuid        primary key,
    tenant_id    uuid        not null,
    consumer     text        not null,
    processed_at timestamptz not null default now()
);
create index inventory_inbox_processed_ix on inventory.inbox_messages (processed_at);

create table fiscal.inbox_messages (like inventory.inbox_messages including all);
create table sales.inbox_messages   (like inventory.inbox_messages including all);

-- =============================================================================
-- 4. CATALOG
--    10M linhas aqui não são problema: lookup por SKU é B-tree (ADR-0012).
-- =============================================================================

create table catalog.products (
    tenant_id     uuid        not null,
    id            uuid        not null default gen_random_uuid(),
    sku           text        not null,
    gtin          text,                              -- EAN/código de barras
    description   text        not null,
    brand         text,
    category_path text,                              -- 'Bebidas/Cervejas/Long Neck'
    unit          text        not null default 'UN',
    ncm           varchar(8),                        -- classificação fiscal
    cest          varchar(7),
    origin        smallint    not null default 0,    -- origem da mercadoria (0-8)
    is_active     boolean     not null default true,
    created_at    timestamptz not null default now(),
    updated_at    timestamptz not null default now(),
    primary key (tenant_id, id),
    constraint products_sku_uk unique (tenant_id, sku)
);

-- Lookup do caixa: GTIN. É o caminho mais quente do catálogo.
create index products_gtin_ix on catalog.products (tenant_id, gtin) where gtin is not null;
create index products_active_ix on catalog.products (tenant_id, is_active) where is_active;

-- =============================================================================
-- 5. PRICING  (ADR-0009)
--    Vigência é do domínio, não do cache: a loja offline aplica a promoção na
--    hora certa porque já recebeu a regra antes de ela valer.
-- =============================================================================

create table pricing.price_lists (
    tenant_id  uuid        not null,
    id         uuid        not null default gen_random_uuid(),
    code       text        not null,
    name       text        not null,
    channel    text        not null default 'pos'
               check (channel in ('pos', 'ecommerce', 'b2b')),
    created_at timestamptz not null default now(),
    primary key (tenant_id, id),
    constraint price_lists_code_uk unique (tenant_id, code)
);

create table pricing.price_rules (
    tenant_id      uuid           not null,
    id             uuid           not null default gen_random_uuid(),
    price_list_id  uuid           not null,
    product_id     uuid           not null,
    store_id       uuid,                              -- null = vale para toda a rede
    min_quantity   numeric(15,4)  not null default 1,
    unit_price     numeric(21,10) not null check (unit_price >= 0),
    valid_from     timestamptz    not null,
    valid_to       timestamptz,                       -- null = sem fim
    priority       int            not null default 0, -- desempate entre regras concorrentes
    created_at     timestamptz    not null default now(),
    primary key (tenant_id, id),
    foreign key (tenant_id, price_list_id) references pricing.price_lists (tenant_id, id),
    constraint price_rules_period_ck check (valid_to is null or valid_to > valid_from)
);

-- Resolução: filtra por produto/loja e recorta por vigência.
create index price_rules_resolution_ix
    on pricing.price_rules (tenant_id, product_id, store_id, valid_from desc, priority desc);

comment on table pricing.price_rules is
    'Regras declarativas. A resolução é função pura sobre elas: mesmo contexto e mesmo '
    'instante produzem sempre o mesmo preço, o que torna auditável "por que saiu por esse valor" (ADR-0009).';

-- =============================================================================
-- 6. INVENTORY  (ADR-0007)
--    Ledger append-only. Nenhum UPDATE de saldo: INSERT não disputa lock de linha.
-- =============================================================================

create table inventory.movements (
    tenant_id    uuid           not null,
    id           uuid           not null default gen_random_uuid(),
    occurred_at  timestamptz    not null default now(),
    store_id     uuid           not null,
    product_id   uuid           not null,
    location     text           not null default 'sales_floor'
                 check (location in ('sales_floor', 'quarantine', 'transit')),
    kind         text           not null
                 check (kind in ('sale', 'purchase', 'adjustment',
                                 'transfer_in', 'transfer_out', 'return', 'loss')),
    quantity     numeric(15,4)  not null,             -- sinalizado: negativo = saída
    reference_id uuid           not null,             -- venda, devolução, nota de entrada
    reason       text,
    created_by   uuid,
    primary key (tenant_id, id, occurred_at)
) partition by range (occurred_at);

create index movements_balance_ix
    on inventory.movements (tenant_id, store_id, product_id, occurred_at);

create table inventory.movements_2026_09 partition of inventory.movements
    for values from ('2026-09-01') to ('2026-10-01');
create table inventory.movements_2026_10 partition of inventory.movements
    for values from ('2026-10-01') to ('2026-11-01');

comment on column inventory.movements.location is
    'Devolução com defeito vai para quarantine, não para o saldo vendável (ADR-0017). '
    'A modelagem completa de localizações ainda está em aberto.';

-- Snapshot: saldo disponível = snapshot + movimentos posteriores - reservas (Redis).
create table inventory.snapshots (
    tenant_id        uuid           not null,
    store_id         uuid           not null,
    product_id       uuid           not null,
    location         text           not null default 'sales_floor',
    quantity         numeric(15,4)  not null,
    last_movement_at timestamptz    not null,   -- marca d'água do que já foi consolidado
    updated_at       timestamptz    not null default now(),
    primary key (tenant_id, store_id, product_id, location)
);

comment on table inventory.snapshots is
    'NUNCA ler direto: o saldo real é snapshot + delta + reservas. Encapsulado em '
    'AvailabilityQuery, senão alguém lê saldo desatualizado (ADR-0007).';

-- =============================================================================
-- 7. CASHIER  (ADR-0018)
--    Venda exige sessão aberta. A invariante é do domínio, não da tela.
-- =============================================================================

create table cashier.sessions (
    tenant_id        uuid           not null,
    id               uuid           not null default gen_random_uuid(),
    store_id         uuid           not null,
    terminal_id      uuid           not null,
    operator_id      uuid           not null,
    status           text           not null default 'open'
                     check (status in ('open', 'closing', 'closed', 'reconciled')),
    opening_float    numeric(15,2)  not null default 0,
    opened_at        timestamptz    not null default now(),
    closed_at        timestamptz,
    closed_by        uuid,
    auto_closed      boolean        not null default false,
    primary key (tenant_id, id),
    foreign key (tenant_id, store_id)    references shared.stores (tenant_id, id),
    foreign key (tenant_id, terminal_id) references shared.terminals (tenant_id, id),
    constraint sessions_closed_ck check (
        (status in ('closed', 'reconciled')) = (closed_at is not null)
    )
);

-- Um terminal não pode ter duas sessões abertas. É o que garante dono único do valor.
create unique index sessions_one_open_per_terminal_uk
    on cashier.sessions (tenant_id, terminal_id) where status in ('open', 'closing');

comment on column cashier.sessions.auto_closed is
    'Fechamento automático por tempo (caixa esquecido aberto) é registrado como tal, '
    'nunca confundido com fechamento conferido (ADR-0018).';

create table cashier.movements (
    tenant_id   uuid           not null,
    id          uuid           not null default gen_random_uuid(),
    session_id  uuid           not null,
    kind        text           not null check (kind in ('withdrawal', 'supply')),
    amount      numeric(15,2)  not null check (amount > 0),
    reason      text           not null,
    authorized_by uuid         not null,
    occurred_at timestamptz    not null default now(),
    primary key (tenant_id, id),
    foreign key (tenant_id, session_id) references cashier.sessions (tenant_id, id)
);

-- Conferência CEGA: declared_amount é gravado ANTES de expected_amount ser revelado.
-- Por meio de pagamento, porque diferenças que se compensam no total são o padrão
-- clássico de desvio (ADR-0018).
create table cashier.closing_counts (
    tenant_id       uuid           not null,
    session_id      uuid           not null,
    payment_method  text           not null,
    declared_amount numeric(15,2)  not null,
    expected_amount numeric(15,2)  not null,
    difference      numeric(15,2)  generated always as (declared_amount - expected_amount) stored,
    justification   text,
    primary key (tenant_id, session_id, payment_method),
    foreign key (tenant_id, session_id) references cashier.sessions (tenant_id, id)
);

comment on column cashier.closing_counts.difference is
    'Sempre registrada, nunca absorvida. Diferença apagada destrói o único indicador '
    'que detecta desvio sistemático (ADR-0018).';

-- =============================================================================
-- 8. SALES  (ADR-0016)
-- =============================================================================

create table sales.sales (
    tenant_id          uuid           not null,
    id                 uuid           not null default gen_random_uuid(),
    created_at         timestamptz    not null default now(),
    store_id           uuid           not null,
    terminal_id        uuid           not null,
    session_id         uuid           not null,     -- ADR-0018: sem sessão não há venda
    operator_id        uuid           not null,
    customer_id        uuid,
    status             text           not null default 'pending'
                       check (status in ('pending', 'completed', 'cancelled')),
    gross_amount       numeric(15,2)  not null default 0,
    discount_amount    numeric(15,2)  not null default 0,
    net_amount         numeric(15,2)  not null default 0,
    exchange_id        uuid,                        -- liga devolução + nova venda (ADR-0017)
    edge_idempotency_key uuid,                      -- rastreia a sincronização do Edge
    primary key (tenant_id, id, created_at)
) partition by range (created_at);

create index sales_store_date_ix on sales.sales (tenant_id, store_id, created_at desc);
create index sales_session_ix    on sales.sales (tenant_id, session_id);
create index sales_exchange_ix   on sales.sales (tenant_id, exchange_id) where exchange_id is not null;

create table sales.sales_2026_09 partition of sales.sales
    for values from ('2026-09-01') to ('2026-10-01');
create table sales.sales_2026_10 partition of sales.sales
    for values from ('2026-10-01') to ('2026-11-01');

-- O item herda o created_at da venda: cai na MESMA partição e viabiliza a FK.
create table sales.sale_items (
    tenant_id       uuid           not null,
    id              uuid           not null default gen_random_uuid(),
    created_at      timestamptz    not null,      -- = sales.created_at, deliberadamente
    sale_id         uuid           not null,
    line_number     int            not null,
    product_id      uuid           not null,
    quantity        numeric(15,4)  not null check (quantity > 0),
    unit_price      numeric(21,10) not null check (unit_price >= 0),
    discount_amount numeric(15,2)  not null default 0,
    net_amount      numeric(15,2)  not null,
    pricing_context jsonb,                         -- quais regras se aplicaram (ADR-0009)
    primary key (tenant_id, id, created_at),
    foreign key (tenant_id, sale_id, created_at)
        references sales.sales (tenant_id, id, created_at)
) partition by range (created_at);

create index sale_items_sale_ix    on sales.sale_items (tenant_id, sale_id);
create index sale_items_product_ix on sales.sale_items (tenant_id, product_id, created_at desc);

create table sales.sale_items_2026_09 partition of sales.sale_items
    for values from ('2026-09-01') to ('2026-10-01');
create table sales.sale_items_2026_10 partition of sales.sale_items
    for values from ('2026-10-01') to ('2026-11-01');

comment on column sales.sale_items.pricing_context is
    'Guardar QUAIS regras se aplicaram, não só o valor final — é o que permite responder '
    '"por que esse item saiu por esse preço" numa reclamação ou auditoria (ADR-0009).';

-- -----------------------------------------------------------------------------
-- 8.1 Devolução — agregado próprio, NÃO venda negativa (ADR-0017)
-- -----------------------------------------------------------------------------
create table sales.returns (
    tenant_id        uuid           not null,
    id               uuid           not null default gen_random_uuid(),
    created_at       timestamptz    not null default now(),
    original_sale_id uuid           not null,
    store_id         uuid           not null,
    session_id       uuid           not null,
    operator_id      uuid           not null,
    authorized_by    uuid,                         -- gerente, acima do limite configurável
    reason           text           not null
                     check (reason in ('defect', 'regret', 'divergence', 'warranty')),
    status           text           not null default 'pending'
                     check (status in ('pending', 'completed', 'refund_failed')),
    total_amount     numeric(15,2)  not null default 0,
    exchange_id      uuid,                         -- presente quando é troca
    primary key (tenant_id, id, created_at)
) partition by range (created_at);

create index returns_original_sale_ix on sales.returns (tenant_id, original_sale_id);
create index returns_exchange_ix on sales.returns (tenant_id, exchange_id) where exchange_id is not null;

create table sales.returns_2026_09 partition of sales.returns
    for values from ('2026-09-01') to ('2026-10-01');
create table sales.returns_2026_10 partition of sales.returns
    for values from ('2026-10-01') to ('2026-11-01');

create table sales.return_items (
    tenant_id   uuid           not null,
    id          uuid           not null default gen_random_uuid(),
    created_at  timestamptz    not null,
    return_id   uuid           not null,
    product_id  uuid           not null,
    quantity    numeric(15,4)  not null check (quantity > 0),
    unit_price  numeric(21,10) not null,
    net_amount  numeric(15,2)  not null,
    to_quarantine boolean      not null default false,
    primary key (tenant_id, id, created_at),
    foreign key (tenant_id, return_id, created_at)
        references sales.returns (tenant_id, id, created_at)
) partition by range (created_at);

create table sales.return_items_2026_09 partition of sales.return_items
    for values from ('2026-09-01') to ('2026-10-01');
create table sales.return_items_2026_10 partition of sales.return_items
    for values from ('2026-10-01') to ('2026-11-01');

comment on column sales.return_items.to_quarantine is
    'Item com defeito não volta ao saldo vendável. Somá-lo ao disponível faz o sistema '
    'mentir sobre estoque e o erro só aparece no balcão (ADR-0017).';

-- -----------------------------------------------------------------------------
-- 8.2 Estado da saga (ADR-0016)
-- -----------------------------------------------------------------------------
create table sales.saga_state (
    tenant_id          uuid        not null,
    sale_id            uuid        not null,
    current_step       text        not null
                       check (current_step in ('pricing', 'reservation', 'payment',
                                               'fiscal', 'confirmation', 'done',
                                               'compensating', 'failed')),
    past_no_return     boolean     not null default false,
    compensation_error text,
    updated_at         timestamptz not null default now(),
    primary key (tenant_id, sale_id)
);

create index saga_stuck_ix on sales.saga_state (updated_at)
    where current_step not in ('done', 'failed');

comment on column sales.saga_state.past_no_return is
    'Marcado quando a NFC-e é emitida. Daí em diante não há compensação técnica, só '
    'processo de negócio: cancelamento na janela da UF ou devolução (ADR-0016).';

-- =============================================================================
-- 9. FISCAL  (ADR-0006)
-- =============================================================================

-- Faixas arrendadas ao Edge. Sem elas não existe emissão offline (ADR-0005).
create table fiscal.number_ranges (
    tenant_id      uuid        not null,
    id             uuid        not null default gen_random_uuid(),
    store_id       uuid        not null,
    model          smallint    not null check (model in (55, 65)),   -- 55 NF-e, 65 NFC-e
    series         int         not null,
    range_start    bigint      not null,
    range_end      bigint      not null,
    next_number    bigint      not null,
    leased_at      timestamptz not null default now(),
    exhausted_at   timestamptz,
    primary key (tenant_id, id),
    constraint number_ranges_bounds_ck check (range_end >= range_start),
    constraint number_ranges_next_ck   check (next_number between range_start and range_end + 1)
);

-- Duas faixas da mesma loja/modelo/série não podem se sobrepor.
create index number_ranges_lookup_ix
    on fiscal.number_ranges (tenant_id, store_id, model, series, range_start);

create table fiscal.documents (
    tenant_id      uuid        not null,
    id             uuid        not null default gen_random_uuid(),
    created_at     timestamptz not null default now(),
    store_id       uuid        not null,
    model          smallint    not null check (model in (55, 65)),
    series         int         not null,
    number         bigint      not null,
    access_key     char(44),                     -- nulo até a chave ser montada
    reference_kind text        not null check (reference_kind in ('sale', 'return', 'transfer', 'purchase')),
    reference_id   uuid        not null,
    emission_type  smallint    not null default 1,   -- 1 normal, 9 contingência offline
    status         text        not null default 'pending'
                   check (status in ('pending', 'authorized', 'rejected',
                                     'cancelled', 'contingency_pending', 'indeterminate')),
    sefaz_status   varchar(3),                   -- cStat: 100 autorizado, 108/109 paralisado, 204 duplicidade
    protocol       varchar(15),
    xml_uri        text,                         -- {tenant}/{cnpj}/{ano}/{mes}/{chave}.xml (ADR-0006)
    transmitted_at timestamptz,
    authorized_at  timestamptz,
    deadline_at    timestamptz,                  -- prazo de transmissão da contingência
    primary key (tenant_id, id, created_at),
    constraint documents_number_ck check (number > 0)
) partition by range (created_at);

-- Numeração sequencial sem colisão: única por loja/modelo/série/número.
create unique index documents_numbering_uk
    on fiscal.documents (tenant_id, store_id, model, series, number, created_at);
create unique index documents_access_key_uk
    on fiscal.documents (tenant_id, access_key, created_at) where access_key is not null;

-- Os dois índices que o Reconciliation Job usa (ADR-0006).
create index documents_indeterminate_ix on fiscal.documents (tenant_id, transmitted_at)
    where status = 'indeterminate';
create index documents_contingency_ix on fiscal.documents (tenant_id, deadline_at)
    where status = 'contingency_pending';

create table fiscal.documents_2026_09 partition of fiscal.documents
    for values from ('2026-09-01') to ('2026-10-01');
create table fiscal.documents_2026_10 partition of fiscal.documents
    for values from ('2026-10-01') to ('2026-11-01');

comment on column fiscal.documents.status is
    'indeterminate = enviado sem resposta conclusiva. NUNCA reemitir a partir desse '
    'estado: consultar a chave primeiro, senão gera duplicidade cStat 204/539 (ADR-0006).';

-- Números concedidos e não usados precisam ser formalmente queimados, senão a
-- numeração tem buracos inexplicados na fiscalização (ADR-0006).
create table fiscal.number_invalidations (
    tenant_id     uuid        not null,
    id            uuid        not null default gen_random_uuid(),
    store_id      uuid        not null,
    model         smallint    not null,
    series        int         not null,
    number_from   bigint      not null,
    number_to     bigint      not null,
    reason        text        not null,
    status        text        not null default 'pending'
                  check (status in ('pending', 'accepted', 'rejected')),
    protocol      varchar(15),
    created_at    timestamptz not null default now(),
    primary key (tenant_id, id)
);

-- Eventos sobre documento existente: cancelamento é evento, não documento novo (ADR-0017).
create table fiscal.document_events (
    tenant_id    uuid        not null,
    id           uuid        not null default gen_random_uuid(),
    access_key   char(44)    not null,
    event_type   text        not null check (event_type in ('cancellation', 'correction_letter')),
    reason       text        not null,
    status       text        not null default 'pending'
                 check (status in ('pending', 'accepted', 'rejected')),
    protocol     varchar(15),
    created_at   timestamptz not null default now(),
    primary key (tenant_id, id)
);

-- Inventário de certificados (ADR-0019). Nenhum material criptográfico aqui.
create table fiscal.certificates (
    tenant_id    uuid        not null,
    id           uuid        not null default gen_random_uuid(),
    store_id     uuid        not null,
    document     varchar(14) not null,          -- CNPJ vinculado
    thumbprint   text        not null,
    not_before   timestamptz not null,
    not_after    timestamptz not null,
    enrolled_at  timestamptz,
    revoked_at   timestamptz,
    primary key (tenant_id, id),
    foreign key (tenant_id, store_id) references shared.stores (tenant_id, id)
);

create index certificates_expiry_ix on fiscal.certificates (not_after)
    where revoked_at is null;

comment on table fiscal.certificates is
    'Apenas inventário: validade, impressão digital e alcance da revogação. A chave '
    'privada vive no repositório protegido do SO na loja e nunca sai de lá (ADR-0019). '
    'Vencimento é PARADA, não degradação: alerta escala a partir de 60 dias.';

-- =============================================================================
-- 10. PAYMENTS
-- =============================================================================

create table payments.transactions (
    tenant_id      uuid           not null,
    id             uuid           not null default gen_random_uuid(),
    created_at     timestamptz    not null default now(),
    sale_id        uuid,
    return_id      uuid,
    method         text           not null
                   check (method in ('cash', 'credit', 'debit', 'pix', 'voucher', 'store_credit')),
    amount         numeric(15,2)  not null check (amount > 0),
    status         text           not null default 'pending'
                   check (status in ('pending', 'approved', 'declined', 'refunded', 'refund_failed')),
    acquirer_nsu   text,                         -- idempotência do lado do adquirente
    authorization_code text,
    card_brand     text,
    installments   smallint       not null default 1,
    primary key (tenant_id, id, created_at),
    constraint transactions_reference_ck check (
        (sale_id is not null) <> (return_id is not null)
    )
) partition by range (created_at);

create index transactions_sale_ix on payments.transactions (tenant_id, sale_id)
    where sale_id is not null;
create unique index transactions_nsu_uk on payments.transactions (tenant_id, acquirer_nsu, created_at)
    where acquirer_nsu is not null;

create table payments.transactions_2026_09 partition of payments.transactions
    for values from ('2026-09-01') to ('2026-10-01');
create table payments.transactions_2026_10 partition of payments.transactions
    for values from ('2026-10-01') to ('2026-11-01');

comment on column payments.transactions.status is
    'refund_failed exige intervenção humana: o adquirente pode recusar o estorno. '
    'Fingir que a compensação sempre funciona é o erro (ADR-0016).';

-- =============================================================================
-- 11. STAGING  (ADR-0015)
--     UNLOGGED: não escreve WAL. Seguro porque o dado é descartável — se o
--     servidor cair, o job é reexecutado.
-- =============================================================================

create unlogged table staging.products_import (
    import_job_id uuid    not null,
    line_number   bigint  not null,
    sku           text,
    gtin          text,
    description   text,
    brand         text,
    category_path text,
    unit          text,
    ncm           text,
    unit_price    text,                          -- texto: validado depois do COPY
    primary key (import_job_id, line_number)
);

create table staging.import_jobs (
    tenant_id       uuid        not null,
    id              uuid        not null default gen_random_uuid(),
    kind            text        not null
                    check (kind in ('products', 'customers', 'prices', 'inventory', 'suppliers')),
    source_uri      text        not null,
    status          text        not null default 'pending'
                    check (status in ('pending', 'running', 'completed', 'failed')),
    total_lines     bigint,
    processed_lines bigint      not null default 0,
    rejected_lines  bigint      not null default 0,
    last_batch      bigint      not null default 0,   -- retomada a partir daqui
    created_at      timestamptz not null default now(),
    finished_at     timestamptz,
    primary key (tenant_id, id)
);

-- Import não é tudo-ou-nada: uma linha ruim não descarta 499.999 boas (ADR-0015).
create table staging.import_rejections (
    tenant_id     uuid        not null,
    import_job_id uuid        not null,
    line_number   bigint      not null,
    raw_line      text,
    error         text        not null,
    primary key (tenant_id, import_job_id, line_number)
);

-- =============================================================================
-- 12. PARTICIONAMENTO AUTOMÁTICO  (ADR-0010)
--
-- Requer pg_partman instalado no servidor. Criação de partição é exatamente o
-- tipo de tarefa que não pode depender de cron artesanal: se falhar, a escrita
-- do sistema quebra à meia-noite do dia 1.
--
--   create extension if not exists pg_partman schema partman;
--
--   select partman.create_parent(
--       p_parent_table => 'sales.sales',
--       p_control      => 'created_at',
--       p_interval     => '1 month',
--       p_premake      => 3);                  -- 3 meses à frente
--
--   -- Retenção: 13 meses (12 não bastam — o comparativo de janeiro perderia
--   -- janeiro do ano anterior no dia 1º).
--   update partman.part_config
--      set retention = '13 months', retention_keep_table = true
--    where parent_table = 'sales.sales';
--
-- Alta rotatividade usa intervalo diário e retenção de 7 dias:
--   shared.idempotency_keys, *.outbox_messages
--
-- Repetir para: sales.sale_items, sales.returns, sales.return_items,
--               inventory.movements, fiscal.documents, payments.transactions
-- =============================================================================

-- =============================================================================
-- 13. RLS — PREPARADO, NÃO HABILITADO  (ADR-0014)
--
-- Hoje há um tenant só e RLS custaria planejamento de query sem benefício.
-- O que JÁ está pronto é a parte cara: tenant_id em toda tabela, primeiro nas
-- PKs e índices compostos. Habilitar depois é migração de mecanismo, sem
-- ALTER TABLE e sem backfill.
--
-- Ao habilitar, três coisas são obrigatórias:
--
-- 1) FORCE, senão o dono da tabela ignora a política:
--      alter table sales.sales enable  row level security;
--      alter table sales.sales force   row level security;
--
-- 2) A política tem de FALHAR FECHADA. Com PgBouncer em transaction pooling
--    (ADR-0011), SET não sobrevive entre comandos — só SET LOCAL, escopo da
--    transação. Uma transação que esqueça o SET LOCAL vê tenant nulo, e nesse
--    caso a política precisa NEGAR TUDO. Política que falha aberta transforma
--    um esquecimento em vazamento silencioso:
--
--      create policy tenant_isolation on sales.sales
--        using (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
--
--    current_setting(..., true) devolve NULL quando não definido; a comparação
--    vira NULL, que não é TRUE — nenhuma linha passa. Fecha por construção.
--
-- 3) O usuário da aplicação NÃO pode ser dono da tabela nem superusuário.
--    Separar o papel de migração do papel de runtime (seção 14).
-- =============================================================================

-- =============================================================================
-- 14. PAPÉIS  (ADR-0011)
--
-- Um usuário por workload: no PgBouncer o pool é por par (usuário, banco), então
-- usuário distinto É o mecanismo de isolamento. O teto baixo do rf_batch é o que
-- impede a carga em massa de consumir as conexões que o caixa precisa.
--
--   rf_migrator  dono dos objetos, roda migração, NÃO roda a aplicação
--   rf_api       40 conexões   primary
--   rf_api_ro    30            réplica
--   rf_fiscal    10            primary
--   rf_worker    20            primary
--   rf_relay      6            primary
--   rf_batch      8            primary   <- teto baixo, deliberado
--   rf_report    10            réplica, somente SELECT
--
--   create role rf_api login password :'rf_api_password';
--   grant usage on schema sales, inventory, catalog, pricing, fiscal,
--                         payments, cashier, shared to rf_api;
--   grant select, insert, update on all tables in schema sales to rf_api;
--   -- sem DELETE: o ledger é append-only (ADR-0007) e nada de transacional
--   -- é apagado pela aplicação; expurgo é por DETACH PARTITION.
-- =============================================================================
