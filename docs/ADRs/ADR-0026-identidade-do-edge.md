# ADR-0026 — Identidade e autenticação do Store Edge

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Não havia modelo de autenticação do Edge. O [ADR-0019](ADR-0019-ciclo-de-vida-certificado-a1.md)
cuida do certificado **fiscal** e menciona "credenciais de sincronização" de passagem,
mas certificado A1 é para assinar documento perante a SEFAZ — não é credencial de acesso
à nossa cloud. São coisas distintas com ciclos de vida distintos.

O modelo ingênuo é `StoreId + senha`. Ele falha em dois momentos previsíveis: quando o
hardware de uma loja é roubado, e quando uma loja troca de equipamento.

## Decisão

### A identidade é do dispositivo, não da loja

```
Device
 ├── DeviceId      identidade própria, imutável
 ├── StoreId       onde está instalado (pode mudar)
 ├── Credential    chave privada gerada no próprio dispositivo
 └── Status        active | suspended | revoked
```

Uma loja pode ter vários dispositivos ao longo do tempo, e revogar um **não** afeta os
outros nem a loja:

```
Store 001 ──┬── Device 123  → revoked (roubado)
            └── Device 456  → active  (substituto)
```

### Provisionamento

1. O Admin gera um **token de enrollment** de uso único, validade curta, vinculado a uma
   loja.
2. Na primeira execução, o Edge **gera um par de chaves localmente**. A chave privada
   nunca sai da máquina e nunca transita pela rede.
3. O Edge se apresenta com o token e a chave pública; a cloud registra o dispositivo e
   devolve o `DeviceId`.
4. O token é queimado.

A partir daí o Edge se autentica provando posse da chave privada, e recebe um **access
token de vida curta**. Nada de segredo compartilhado de longa duração.

### Revogação

`status = revoked` é suficiente para o dispositivo parar de sincronizar na próxima
tentativa. Não depende de alcançar a máquina.

Revogação **não** apaga o histórico: as vendas que aquele dispositivo já sincronizou
continuam válidas e atribuídas a ele. Isso é registro, não permissão.

### Duas credenciais, dois ciclos de vida

| | Certificado A1 | Identidade do dispositivo |
|---|---|---|
| Para quê | Assinar documento fiscal perante a SEFAZ | Autenticar na nossa cloud |
| Vinculado a | CNPJ | Dispositivo físico |
| Emitido por | AC da ICP-Brasil | Nós |
| Validade | 1 ano, compra anual | Chave longa, token curto |
| Revogação | Junto à AC | Uma linha no nosso banco |

Não se confundem e não se substituem. O A1 continua governado pelo
[ADR-0019](ADR-0019-ciclo-de-vida-certificado-a1.md).

### Rotação e detecção

- Access token de vida curta, renovado com a chave do dispositivo.
- Rotação da chave do dispositivo disponível como operação, sem re-enrollment presencial.
- Alerta de uso do **mesmo `DeviceId` a partir de mais de uma origem** — indica clonagem.
- Alerta de dispositivo `active` sem `LastSeen` há muito tempo — pode ser loja fechada,
  pode ser equipamento desviado.

## Por quê

**Por que a chave nasce no dispositivo.** Se a cloud gerasse e enviasse a chave privada,
ela existiria em trânsito e possivelmente em log ou backup. Gerando localmente, só a
pública circula.

**Por que identidade de dispositivo e não de loja.** A pergunta operacional na revogação é
"qual máquina tratar", e a resposta é por dispositivo. Com credencial por loja, revogar
uma máquina roubada derrubaria a loja inteira — inclusive o equipamento substituto.

**Por que token curto em vez de credencial longa.** Vazamento de token expira sozinho;
vazamento de credencial longa vale até alguém perceber.

**Por que revogar não apaga histórico.** Um dispositivo revogado por roubo emitiu vendas
legítimas antes do roubo. Apagar a atribuição destruiria a trilha de auditoria justamente
no incidente em que ela mais importa.

## Alternativas descartadas

- **`StoreId` + senha compartilhada.** Rejeitada: roubo obriga a trocar a senha da loja —
  e provavelmente ela é a mesma em toda a rede.
- **Reaproveitar o certificado A1 como credencial de acesso.** Tentador, já que ele está
  lá. Rejeitada: ciclos de vida diferentes (o A1 vence anualmente e é comprado), e
  vincular acesso ao CNPJ impediria distinguir dois dispositivos da mesma loja.
- **mTLS com certificado emitido por CA interna.** Boa alternativa, tecnicamente
  equivalente. Rejeitada por ora pelo custo de operar uma CA própria; reconsiderar se o
  cliente exigir mTLS fim a fim.
- **Identidade por hash de hardware.** Rejeitada: quebra em troca de peça e é trivial de
  forjar.

## Consequências

**Positivas**
- Revogação é uma linha no banco, com alcance exato de um dispositivo.
- Nenhum segredo de longa duração trafega.
- Substituir equipamento é enrollment novo, sem tocar em nada da loja.

**Negativas**
- **Mais um subsistema**: enrollment, inventário de dispositivos, emissão e renovação de
  token, rotação, revogação. Precisa existir antes da primeira loja.
- Perder o token de enrollment antes de usar exige gerar outro — atrito na instalação.
- A chave privada do dispositivo está no disco de uma máquina em loja. Disco cifrado é
  obrigatório, como já exige o [ADR-0019](ADR-0019-ciclo-de-vida-certificado-a1.md).
- Token curto exige que o Edge lide com renovação **e** com estar offline durante ela —
  ou seja, operar com token expirado até reconectar, sem bloquear a venda.
