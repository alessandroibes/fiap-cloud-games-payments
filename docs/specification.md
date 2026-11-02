# SPEC-P2-PAYMENTS — PaymentsAPI

**Estado:** G4 implementado; 45 testes locais aprovados. [Evidências](quality/g4-validation.md). Notifications real no G5, Compose completo no G6 e Kubernetes no G7.

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
Consome [OrderPlacedEvent](contracts/v1/order-placed-v1.md); publica [PaymentProcessedEvent](contracts/v1/payment-processed-v1.md) via Consumer Outbox EF/MassTransit, com filas independentes de Catalog e Notifications. Nenhum acesso aos bancos irmãos.

## Decisões confirmadas e detalhamento técnico
Migrations explícitas antes das APIs confirmadas em 30/10/2026. Configuração PaymentSimulation:Outcome é Approved por padrão ou Rejected; valores inválidos impedem startup. Unicidade por OrderId, receipts duráveis por EventId e locks transacionais protegem duplicatas; resultados persistidos não são recalculados após mudança de modo. Erros permanentes não são retentados; falhas transitórias têm três retentativas em 1, 5 e 15 segundos. [Detalhes operacionais e recuperação](operations.md).

## Baseline obrigatória para implementação

As decisões dos ADR-P2-001 a ADR-P2-007 estão **Aceitas**. PostgreSQL implementado; Docker Desktop Kubernetes previsto no G7. Contratos V1 em `docs/contracts/v1/`.
