# SPEC-P2-PAYMENTS — PaymentsAPI

**Estado:** Especificação de planejamento, alinhada aos ADRs aceitos; implementação ainda não iniciada.

## Responsabilidade
Consumir pedidos e simular processamento de pagamento; não conceder jogos nem acessar dados de catálogo diretamente.

## Fluxo e aceite
1. Consumir `OrderPlacedEvent` e correlacionar pelo `OrderId`.
2. Simular resultado determinístico por configuração, padrão `Approved` e modo `Rejected` para testes, sem controle pelo comprador, conforme ADR-P2-005.
3. Publicar `PaymentProcessedEvent` com status literal `Approved`/`Rejected`.
4. Mensagem repetida do mesmo pedido não cria resultados contraditórios nem múltiplos processamentos lógicos.
5. Falhas transitórias seguem retry limitado; erros permanentes são observáveis e recuperáveis.
6. Testes unitários cobrem ambos os resultados; integração verifica publicação e duplicatas.

## Integrações
Consome `order-placed-v1.md`; publica `payment-processed-v1.md`.

## Decisões confirmadas e detalhamento técnico
Migrations explícitas antes das APIs confirmadas em 30/10/2026. Detalhar configuração técnica do transporte e mensagens inválidas. PostgreSQL, resultado único por OrderId, Outbox/Inbox e simulação determinística já aprovados.

## Baseline obrigatória para implementação

As decisões dos ADR-P2-001 a ADR-P2-007 estão **Aceitas**. Implementar com PostgreSQL e Docker Desktop Kubernetes; contratos V1 conforme `docs/contracts/`.
