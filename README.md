# fiap-cloud-games-payments

Pagamento simulado determinístico, Inbox/Outbox e PaymentProcessedEvent.

## Estado

G4 implementado em .NET 8, PostgreSQL/EF Core e MassTransit/RabbitMQ. Processamento determinístico de pedidos com Consumer Outbox, Inbox e resultado único por OrderId. Suíte local: 45 testes aprovados. [Evidências e limites](docs/quality/g4-validation.md).

Notifications real pertence ao G5; Compose completo ao G6 e manifests /k8s ao G7, usando Docker Desktop Kubernetes.

## Documentação

- [ADRs aceitos](docs/architecture/decisions/README.md).
- [Decisões funcionais confirmadas](docs/architecture/functional-decisions.md).
- [Especificação de referência](docs/specification.md).
- [Operação local, simulação e recuperação](docs/operations.md).
- [OrderPlacedEvent V1](docs/contracts/v1/order-placed-v1.md) e [PaymentProcessedEvent V1](docs/contracts/v1/payment-processed-v1.md).
- Contratos canônicos e roadmap: repositório fiap-cloud-games-infrastructure, docs/contracts/v1/ e docs/implementation/roadmap.md. Tipos C# locais, sem dependência de projetos irmãos.

## Execução e configuração

Com infraestrutura G1 em execução, na raiz deste repositório:

```powershell
./scripts/Initialize-Local.ps1
docker compose build payments
docker compose run --rm payments --migrate
docker compose up -d payments
```

Saúde em http://localhost:5083/health/live e /health/ready. Não há endpoints HTTP de pagamento; entrada e saída ocorrem pelo broker. Configuração em .env ignorado pelo Git, com credencial exclusiva de Payments. Não há migration automática no startup.

PaymentSimulation__Outcome aceita Approved (padrão) ou Rejected. O modo controla novos pedidos e exige reinício após alteração. O resultado já persistido não muda com a configuração; o comprador não escolhe o modo.

```powershell
./scripts/Validate-G4.ps1
```

Testes usam banco e broker próprios, portas 55435 e 5675; interrompem somente seu broker e recriam filas de teste. Não executar suítes simultâneas. Para demonstração com Users/Catalog reais, consultar scripts/Validate-LocalFlow.ps1 e as condições de execução em [operação](docs/operations.md).

## Fluxo e organização

OrderPlacedEvent chega em payments-order-placed-v1. Pagamento, receipt e PaymentProcessedEvent são confirmados na mesma transação. Resultado é entregue independentemente às filas de Catalog e Notifications. Duplicatas não geram novo pagamento; conflitos/invalidade seguem para _error. Falhas transitórias têm três retentativas limitadas antes da fila de erro.

- Domain: Payment, snapshot do pedido e validações monetárias/identificadores.
- Application: política determinística, serviço e porta de persistência.
- Infrastructure: PostgreSQL, migration, Inbox/Outbox e consumer.
- Api: health checks e comando explícito de migration.
- Contracts e tests: V1 local, assembly independente e integração real com injeção de falhas.

## Git e segurança

Clone independente fornecido pelo responsável; manter o origin existente. Commits separados, sem push ou publicação de imagens sem autorização. Documentação técnica é versionada. AGENTS.md, specs/, .codex/ e docs/course/ são locais e ignorados; segredos fora do Git.

## Kubernetes local

Manifestos e sequência de implantação em [k8s/README.md](k8s/README.md). G7 concluído; [evidências deste serviço](docs/quality/g7-validation.md) e registro transversal na infraestrutura.

## Resiliência — G8

Regressão concluída com 45 testes aprovados; [evidências e limites](docs/quality/g8-validation.md). Recuperação integrada no Kubernetes registrada na infraestrutura. Próxima etapa: entrega acadêmica G9.
