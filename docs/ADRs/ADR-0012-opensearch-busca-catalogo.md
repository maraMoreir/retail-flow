# ADR-0012 — OpenSearch para busca de catálogo; PostgreSQL continua a fonte da verdade

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Elasticsearch aparecia no roadmap futuro. Com a premissa P4 (milhões de SKUs), é preciso
separar dois problemas que costumam ser confundidos:

**Lookup por identificador** — "produto do SKU 7891234567890", "produto do EAN lido pelo
scanner". Índice B-tree, tempo sub-milissegundo em 10 milhões de linhas. O PostgreSQL
resolve sem esforço e **não precisa de nada além dele**.

**Busca de catálogo** — "camiseta azul algodão", com correção de digitação, sinônimos,
ranking por relevância, facetas por marca/categoria/faixa de preço/disponibilidade, e
paginação profunda. O PostgreSQL com `tsvector` faz uma versão disso, mas: o ranking é
`ts_rank`, sem controle fino; facetas exigem agregações caras; tolerância a erro de
digitação depende de `pg_trgm` com custo alto; e sinônimo é uma tabela mantida à mão.

## Decisão

**OpenSearch** para busca de catálogo, alimentado por evento. O PostgreSQL permanece a
fonte da verdade — OpenSearch é um índice descartável e reconstruível.

**Pipeline de indexação:** alteração em `catalog.products` ou `pricing.rules` grava na
outbox → `catalog.products.changed.v1` → worker de indexação atualiza o documento.

**O documento é desnormalizado** e carrega o que a busca precisa filtrar e ordenar:
atributos do produto, categoria, marca, faixa de preço por região e flag de
disponibilidade agregada. Nunca o preço exato transacionado — preço vem de Pricing
([ADR-0009](ADR-0009-pricing-contexto-cache.md)); no índice ele é só critério de
filtro e ordenação.

**Reindexação completa** é um procedimento suportado e testado: lê do PostgreSQL, escreve
em índice novo, troca o alias atomicamente. Roda em ambiente de homologação
periodicamente, para que não seja uma operação desconhecida no dia em que for necessária.

**O que continua no PostgreSQL:** lookup por SKU/EAN (caminho do caixa), qualquer consulta
que precise de consistência forte, e toda escrita.

**O caixa nunca depende do OpenSearch.** A leitura de item no PDV é lookup por código de
barras, resolvido no cache do Edge. OpenSearch fora = busca do Admin e da vitrine
degradadas; o caixa não percebe.

### Isolamento por tenant: alias por tenant, decidido agora

Ao contrário do PostgreSQL, **não existe RLS aqui**. Índice compartilhado com filtro de
tenant na query é um desenho que **falha aberto**: uma consulta que esqueça o filtro
devolve o catálogo de outro cliente, sem nenhuma barreira do motor.

Decisão: **um alias por tenant** (`catalog-{tenant}` → índice físico versionado), resolvido
no cliente a partir do `ITenantContext` — **nunca** a partir de entrada da requisição.

Com um tenant só isso é um alias apontando para um índice, custo zero. O que compramos é
o modo de falha: consultar o alias errado devolve **nada**, não devolve dado alheio.
Convenção adotada desde já, para que a migração de [ADR-0014](ADR-0014-tenantid-dormente.md)
não precise reescrever a camada de busca. Reavaliar se o número de tenants crescer a ponto
de a contagem de shards pesar — aí o caminho é agrupar tenants pequenos num índice
compartilhado, decisão que se toma com dados de uso.

## Por quê

**Por que um segundo datastore.** É a escolha deliberada de pagar consistência eventual e
operação extra em troca de uma capacidade que o Postgres não tem. O critério: busca
facetada com ranking sobre catálogo grande é um problema de motor de busca, não de banco
relacional. Tentar resolver com `tsvector` + `pg_trgm` + agregações funciona até alguns
milhões de documentos e depois vira um problema de tuning permanente que compete com a
carga transacional no mesmo banco.

**Por que OpenSearch e não Elasticsearch.** Licença. Elasticsearch mudou para SSPL/ELv2 em
2021; OpenSearch é o fork Apache 2.0. Tecnicamente equivalentes para este uso, e a licença
permissiva evita uma discussão jurídica futura.

**Por que alimentar por evento e não por sincronização periódica.** A outbox
([ADR-0003](ADR-0003-outbox-inbox.md)) já garante que nenhuma alteração se perde.
Sincronização periódica teria latência de propagação e um job que compara estados —
complexo e caro para milhões de documentos.

**Por que o índice é descartável.** Todo estado do OpenSearch é derivado do PostgreSQL.
Isso muda a postura operacional: não precisa de backup, e "reindexar do zero" é uma
resposta legítima a qualquer inconsistência. Se o índice fosse fonte da verdade de algo,
essa saída não existiria.

**Por que o caixa não pode depender dele.** A consistência é eventual e o cluster é mais
uma peça que pode cair. Nada no caminho crítico da venda pode depender de um índice
eventualmente consistente.

## Alternativas descartadas

- **`tsvector` no PostgreSQL.** Rejeitada para busca facetada (ver acima). **Mantida** para
  qualquer busca simples em tabelas pequenas — não vale subir OpenSearch para buscar
  fornecedor.
- **Elasticsearch.** Rejeitada por licença.
- **Algolia / Typesense / Meilisearch.** Não rejeitadas por mérito técnico. Algolia reduz
  muito a operação, ao custo de SaaS por operação — reavaliar se o time for pequeno.
  Meilisearch é excelente mas menos maduro em facetas complexas e escala horizontal.
- **Índice como fonte da verdade.** Rejeitada categoricamente: motor de busca não é banco
  de dados transacional.

## Consequências

**Positivas**
- Busca de catálogo boa de verdade, sem competir com a carga transacional.
- Índice descartável: "reindexar" é sempre uma saída válida.
- Carga analítica de vitrine sai do Postgres.

**Negativas**
- **Mais um cluster para operar**, dimensionar e monitorar. É o custo principal.
- **Consistência eventual visível**: produto alterado aparece na busca com atraso de
  segundos. Precisa ser aceito pelo negócio — em particular, o Admin não deve mostrar
  resultado de busca logo após um cadastro sem avisar disso.
- Documento desnormalizado precisa ser reindexado quando **qualquer** fonte muda —
  incluindo preço e disponibilidade, que mudam com frequência muito maior que o cadastro.
  Isso pode gerar volume de indexação alto; pode ser necessário agrupar atualizações.
- O mapeamento do índice vira artefato versionado, com migração própria. Mudança de
  mapeamento geralmente exige reindexação completa.
- **Não há RLS aqui**, então o isolamento entre tenants é responsabilidade da convenção de
  alias (ver acima), não do motor. Um alias por tenant multiplica shards; com muitos
  tenants pequenos isso vira custo real e exige reavaliação.
