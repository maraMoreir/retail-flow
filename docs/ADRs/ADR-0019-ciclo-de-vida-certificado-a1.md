# ADR-0019 — Ciclo de vida do certificado A1 nas lojas

**Status:** Proposto · **Data:** 2026-09-30

## Contexto

Pendência aberta e pré-requisito para a primeira loja em produção. O
[ADR-0005](ADR-0005-store-edge-operacao-offline.md) aceitou que o certificado A1 vive na
loja — consequência inevitável de emitir NFC-e em contingência offline
([ADR-0006](ADR-0006-fiscal-contexto-isolado.md)): se o link caiu, um assinador remoto
também está inalcançável.

O ADR-0005 listou mitigações mas não definiu o ciclo de vida. Sem isso: alguém copia o
`.pfx` num pendrive, a senha vai por WhatsApp, o certificado vence num sábado e a rede
inteira para.

O certificado A1 é emitido por uma AC da ICP-Brasil, vinculado a um CNPJ, com validade de
um ano e **sem renovação automática**. Não há como emiti-lo internamente.

## Decisão

### Um certificado por CNPJ, nunca compartilhado

Cada filial com CNPJ próprio tem seu próprio A1. Lojas que compartilham CNPJ compartilham
o certificado — mas o **inventário é por loja**, para que a revogação saiba o alcance.

### Provisionamento por enrollment, nunca por cópia manual

O Edge nasce sem certificado. O fluxo:

1. O Admin gera um **token de enrollment** de uso único, com validade curta, vinculado a
   uma loja específica.
2. O técnico informa o token na primeira execução do Edge.
3. O Edge se autentica, recebe o certificado por canal cifrado e o instala **direto no
   repositório protegido do SO** (DPAPI no Windows, com escopo de máquina).
4. O token é queimado. O material do certificado **nunca toca o disco em claro** e nunca
   passa por e-mail, chat ou pendrive.

O Edge nunca exporta a chave privada. A senha do `.pfx` existe só no cofre central.

### Rotação com janela de sobreposição

O certificado tem um ano. A rotação é orquestrada pela cloud:

| Antecedência | Ação |
|---|---|
| 60 dias | Alerta de compra no Admin |
| 30 dias | Novo certificado disponível no cofre; distribuição começa |
| 15 dias | Alerta escalado; lojas ainda não rotacionadas são listadas nominalmente |
| Vencido | Loja cai em contingência permanente — e contingência **também exige assinar** |

O novo certificado é instalado **ao lado** do antigo e só passa a ser usado na data de
virada. Nunca há um instante sem certificado válido instalado.

> **O vencimento não é degradação gradual, é parada.** Contingência offline não é saída,
> porque ela também assina. Certificado vencido = loja não emite = loja não vende. Por
> isso o alerta começa com 60 dias e escala nominalmente.

### Revogação e descomissionamento

Perda de controle do hardware ou desligamento de loja dispara: revogação junto à AC,
desabilitação do enrollment daquela loja, invalidação das credenciais de sincronização e
apagamento remoto do repositório protegido quando o Edge estiver alcançável — **sem
depender** desse apagamento para considerar a revogação efetiva.

### Detecção de uso indevido

- Disco cifrado no hardware do Edge, como já exigia o ADR-0005.
- Alerta de assinatura fora do horário de funcionamento da loja.
- Alerta de uso do mesmo certificado a partir de mais de um Edge.
- Inventário no Admin: loja, CNPJ, validade, data da última rotação, impressão digital.

## Por quê

**Por que enrollment com token e não instalação manual.** Instalação manual significa o
`.pfx` e a senha circulando por canais não auditáveis, e a senha costuma acabar num
documento compartilhado. O enrollment torna a distribuição auditável (quem, quando, qual
loja) e faz o material nunca existir em claro fora do repositório do SO.

**Por que janela de sobreposição e não troca no dia.** Troca na data de vencimento
significa que qualquer falha de distribuição — loja offline, hardware trocado, técnico
ausente — para a loja. Com 30 dias de sobreposição, a distribuição pode falhar e ser
repetida sem consequência.

**Por que o inventário é por loja e não por CNPJ.** Na revogação, a pergunta é "quais
máquinas precisam ser tratadas", e a resposta é por loja. Um inventário por CNPJ não
responde isso quando duas lojas compartilham o mesmo.

**Por que não depender do apagamento remoto.** Se o hardware foi perdido, ele
provavelmente não vai se conectar de novo. A revogação junto à AC é o que efetivamente
invalida o certificado; o apagamento é higiene adicional, não a barreira.

## Alternativas descartadas

- **Assinatura centralizada na cloud (HSM).** Mais segura e a preferida em qualquer outro
  cenário. **Incompatível com P1**: a contingência offline exige assinar sem rede.
- **Certificado A3 em token físico na loja.** Mais seguro contra cópia, mas exige presença
  física para cada operação de gestão e o token quebra, some e trava por PIN errado.
  Operacionalmente inviável numa rede.
- **Um certificado para a rede toda.** Rejeitada: revogação pararia todas as lojas, e o
  vínculo com CNPJ não permitiria emissão correta por filial.
- **Instalação manual documentada em runbook.** Rejeitada: não é auditável e a senha
  sempre vaza para um canal informal.

## Consequências

**Positivas**
- Distribuição auditável; chave privada nunca em claro fora do repositório protegido.
- Vencimento vira alerta escalado com 60 dias, não incidente.
- Revogação tem alcance conhecido e procedimento definido.

**Negativas**
- **Enrollment é um subsistema para construir**: emissão de token, canal cifrado,
  instalação no repositório do SO, inventário. Não é trivial e precisa estar pronto antes
  da primeira loja.
- Amarra o Edge ao Windows, se a proteção for DPAPI. Suportar Linux exige outro mecanismo
  — decidir isso junto com o hardware da loja.
- **A compra do certificado continua sendo um passo humano e anual.** O sistema alerta,
  mas não compra. Precisa de dono nomeado no runbook.
- Troca de hardware da loja exige re-enrollment. Deve estar no procedimento de
  substituição, senão a loja volta sem conseguir emitir.
