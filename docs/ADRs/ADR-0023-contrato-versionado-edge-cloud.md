# ADR-0023 — Contrato versionado entre Edge e Cloud

**Status:** Proposto · **Data:** 2026-09-30
**Relacionado:** [ADR-0025](ADR-0025-atualizacao-frota-edge.md) — a janela de suporte e a política de rollout são a mesma decisão

## Contexto

O Edge é uma frota. Rollout escalonado significa, por definição, versões diferentes
falando com a mesma cloud ao mesmo tempo. Uma loja pode ficar semanas offline e voltar
com uma versão antiga.

Não havia nada escrito sobre isso: nem por quantas versões a cloud mantém
compatibilidade, nem como um campo novo é introduzido, nem o que acontece com um Edge
muito atrasado.

## Decisão

### A versão do contrato é independente da versão do Edge

```
Edge v5.3  ──Contract v2──►  Cloud  ──┬── aceita Contract v1
                                      └── aceita Contract v2
```

Atrelar a versão da API à versão do binário obrigaria a bumpar o contrato a cada correção
de bug no Edge, e cada bump é trabalho de compatibilidade na cloud.

Toda requisição carrega:

```http
POST /api/v2/sync/sales
X-Contract-Version: 2
X-Client-Version: 5.3.0
X-Device-Id: 8f3a...
```

`X-Client-Version` é diagnóstico, nunca decisão de roteamento.

### Regra de evolução

| Mudança | Versão | Regra |
|---|---|---|
| Campo opcional novo | minor (`v1.1`) | Compatível. Cliente antigo ignora |
| Campo opcional vira obrigatório | **major** | Breaking |
| Remoção de campo | **major** | Nunca imediata — ver abaixo |
| Mudança de semântica do mesmo campo | **major** | O pior tipo: passa despercebido |

**Nunca remover um campo na mesma versão em que o substituto é introduzido.** Os dois
coexistem por uma major inteira:

```
v1:   customerTaxId
v2:   customerTaxId (deprecado, ainda preenchido) + customerDocumentType
v3:   customerDocumentType
```

### Janela de suporte

A cloud mantém **a major corrente e as duas anteriores**. O número não é arbitrário —
sai da conta de [ADR-0025](ADR-0025-atualizacao-frota-edge.md):

```
janela ≥ duração do rollout completo pelos rings
       + período máximo plausível de loja offline
       + margem operacional
```

Mudar a cadência de rollout **muda a janela de suporte**. São a mesma decisão.

### Canal de recuperação sem versão

Um endpoint que **nunca** entra em breaking change, cujo contrato é congelado:

```http
GET /device/recovery
→ { "supported": false, "minContractVersion": 2, "updatePackageUri": "..." }
```

Sem ele existe deadlock: o Edge com contrato expirado é rejeitado na sincronização, e é
exatamente pela sincronização que ele receberia a atualização. A loja viraria tijolo,
resolvido só com visita presencial.

Esse endpoint não carrega dado de negócio — só identidade do dispositivo e ponteiro de
atualização. É o que permite congelá-lo para sempre.

### Rejeição é explícita

Contrato incompatível devolve `426 Upgrade Required` com a versão mínima e o ponteiro de
recuperação. Nunca `400` genérico, nunca aceitar e interpretar torto.

### Adaptadores de contrato na cloud

```
             ┌── v1 adapter ──┐
Requisição ──┤                ├──► modelo interno
             └── v2 adapter ──┘
```

O domínio conhece **um** modelo. A tradução vive na borda. Sem isso, `if (contractVersion
== 1)` se espalha pelos handlers.

## Por quê

**Por que desacoplar as versões.** A versão do binário muda por qualquer correção; a do
contrato só deve mudar quando a conversa muda. Acoplar as duas gera bump de contrato sem
mudança de contrato — e cada bump custa uma janela de compatibilidade.

**Por que o substituto coexiste por uma major inteira.** Porque a frota não atualiza junto.
Remover o campo antigo junto com a introdução do novo quebra todo Edge que ainda não
atualizou — que é a maioria, no dia do deploy.

**Por que mudança de semântica é breaking mesmo sem mudar o schema.** É o caso mais
perigoso: o campo continua lá, com o mesmo tipo, significando outra coisa. Nada quebra
visivelmente; os dados ficam errados em silêncio.

**Por que adaptador por versão e não condicionais.** Um `if` por versão em cada handler
espalha o conhecimento de compatibilidade pelo domínio inteiro, e aposentar uma versão
vira caça a condicionais. Com adaptador, aposentar é apagar uma classe.

## Alternativas descartadas

- **Versão só no path (`/api/v2/…`), sem header.** Rejeitada: obriga a duplicar a árvore
  de rotas e não carrega a versão do cliente, que é diagnóstico essencial. Mantemos a
  versão no path *e* no header por clareza, mas a autoritativa é o header.
- **Um único contrato "sempre compatível", sem versão.** Rejeitada: funciona até a
  primeira mudança semântica, e aí falha em silêncio.
- **Suportar todas as versões para sempre.** Rejeitada: o custo de manutenção cresce sem
  limite e nunca se apaga código.
- **Forçar o Edge a atualizar antes de qualquer sincronização.** Rejeitada: a loja ficaria
  parada esperando download. A venda offline é o produto — nada pode bloqueá-la.

## Consequências

**Positivas**
- Rollout escalonado deixa de ser risco e vira operação normal.
- Aposentar uma versão é apagar um adaptador.
- Loja muito atrasada tem caminho de volta sem visita presencial.

**Negativas**
- **Dois a três adaptadores de contrato vivos ao mesmo tempo**, todos precisando de teste.
  A matriz de teste multiplica pelo número de versões suportadas.
- O canal de recuperação é congelado **para sempre**. Qualquer erro no desenho dele é
  permanente — merece revisão desproporcional ao seu tamanho.
- Campos deprecados ficam sendo preenchidos por uma major inteira, o que significa código
  morto vivo e pressão recorrente para remover antes da hora.
- A janela de suporte amarra a decisão de rollout. Encurtar a cadência de atualização
  exige revisar este ADR junto.
