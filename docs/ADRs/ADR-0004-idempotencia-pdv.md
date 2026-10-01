# ADR-0004 — `Idempotency-Key` obrigatória em toda escrita vinda do PDV

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

O link de loja de varejo é ruim: 4G de contingência, link compartilhado com a rede da loja,
roteador barato. O Store Edge vai sofrer timeout ao sincronizar vendas com a cloud, e vai
reenviar — é o comportamento correto de um cliente resiliente.

Sem tratamento, um timeout de rede na resposta (a requisição chegou e foi processada, mas a
resposta se perdeu) produz: venda duplicada, baixa de estoque duplicada, apuração duplicada.
E na reconciliação de contingência o mesmo lote de vendas pode ser reenviado inteiro.

Isso não é caso de borda. É rotina diária numa rede de lojas.

## Decisão

Todo endpoint de escrita não-naturalmente-idempotente exige o header `Idempotency-Key`
(UUIDv7 gerado pelo Edge, estável entre tentativas).

Um endpoint filter do ASP.NET Core, executado **antes** do handler:

1. Calcula `hash = SHA256(corpo da requisição)`.
2. Tenta `INSERT` em `shared.idempotency_keys (key, endpoint, request_hash, status)` com
   `status = 'in_flight'`.
3. **Conflito com `status = 'completed'` e mesmo hash** → devolve a resposta gravada, com
   o status original.
4. **Conflito com hash diferente** → `409 Conflict`. Mesma chave, corpo diferente é bug do
   cliente e não pode ser tratado como retry.
5. **Conflito com `status = 'in_flight'`** → `409` com `Retry-After`. Há uma tentativa em
   andamento.
6. Sucesso → executa o handler, grava a resposta e marca `completed`, **na mesma transação
   do handler**.

```sql
create table shared.idempotency_keys (
  tenant_id     uuid        not null,
  key           uuid        not null,          -- gerada pelo cliente
  endpoint      text        not null,
  request_hash  bytea       not null,
  status        text        not null,          -- in_flight | completed
  response_code int,
  response_body jsonb,
  created_at    timestamptz not null default now(),
  expires_at    timestamptz not null,
  primary key (tenant_id, key)
);
create index on shared.idempotency_keys (expires_at);
```

> **A chave primária é composta com `tenant_id` de propósito.** A `key` é gerada pelo
> **cliente**, não por nós — então nada garante unicidade entre tenants. Com `key` sozinha
> como PK, um tenant que enviasse uma chave já usada por outro receberia a **resposta
> gravada do outro tenant**, incluindo o corpo. É vazamento de dado entre clientes, não
> apenas colisão. Com um tenant só isso é inofensivo, mas a coluna precisa estar na PK
> desde agora: alterar chave primária de uma tabela de alta rotatividade em produção é
> caro. Ver [ADR-0014](ADR-0014-tenantid-dormente.md).

TTL de 7 dias — cobre com folga uma contingência longa. Expurgo por job.

## Por quê

**A chave tem que vir do cliente**, não ser gerada no servidor, porque só o cliente sabe
que duas requisições são *a mesma tentativa*. Servidor gerando chave não resolve nada.

**Gravar a chave na mesma transação do efeito** é o detalhe que faz funcionar. Se a chave
fosse gravada em transação separada (ou no Redis), existiria a mesma janela de dual-write
que o [ADR-0003](ADR-0003-outbox-inbox.md) elimina: chave gravada e handler revertido, ou
handler efetivado e chave perdida.

**Hash do corpo** protege contra reuso acidental de chave. Sem ele, um bug no Edge que
reaproveite a chave faz a segunda venda ser silenciosamente descartada e o operador recebe
a confirmação da *primeira* — o pior resultado possível, porque é invisível.

**Por que PostgreSQL e não Redis:** a garantia precisa ser transacional com o efeito.
Redis é rápido, mas não participa da transação do Postgres, o que reintroduz o dual-write.

## Alternativas descartadas

- **Dedupe por hash do conteúdo da venda.** Rejeitada: duas vendas legitimamente idênticas
  (mesmo item, mesmo valor, mesmo minuto, caixas diferentes) seriam colapsadas em uma.
- **Chave só no Redis com TTL.** Rejeitada: sem garantia transacional.
- **`PUT /sales/{id}` com id gerado pelo cliente.** Idempotência natural e elegante, mas não
  cobre os outros endpoints de escrita (pagamento, devolução, sangria), e não guarda a
  resposta original para devolver no retry. O filtro genérico cobre todos uniformemente.

## Consequências

**Positivas**
- O Edge pode reenviar livremente, o que simplifica muito a lógica de sincronização dele.
- A tabela vira evidência auditável de quais tentativas foram vistas e quando.

**Negativas**
- Uma escrita e uma leitura a mais por requisição de escrita. Medível, mas pequeno perto do
  custo de uma venda duplicada.
- A tabela tem alta rotatividade (insert + update + delete). Precisa de particionamento por
  data e `autovacuum` agressivo, ou vira fonte de bloat.
- `in_flight` preso por processo morto bloqueia retries até o TTL. Mitigar com um timeout
  curto de `in_flight` (ex.: 60s) que libera a chave.
