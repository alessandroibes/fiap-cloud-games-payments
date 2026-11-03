# fiap-cloud-games-payments

Pagamento simulado determinístico, Inbox/Outbox e PaymentProcessedEvent.

## Estado

G4 implementado em .NET 8, PostgreSQL/EF Core e MassTransit/RabbitMQ. Processamento determinístico de pedidos com Consumer Outbox, Inbox e resultado único por OrderId. Suíte local: 45 testes aprovados. [Evidências e limites](docs/quality/g4-validation.md).

Integração com Notifications validada no Compose (G6) e Kubernetes Docker Desktop (G7); regressão de resiliência concluída no G8.

## Documentação

- [ADRs aceitos](docs/architecture/decisions/README.md).
- [Decisões funcionais confirmadas](docs/architecture/functional-decisions.md).
- [Especificação de referência](docs/specification.md).
- [Operação local, simulação e recuperação](docs/operations.md).
- [OrderPlacedEvent V1](docs/contracts/v1/order-placed-v1.md) e [PaymentProcessedEvent V1](docs/contracts/v1/payment-processed-v1.md).
- Contratos canônicos e roadmap: repositório fiap-cloud-games-infrastructure, docs/contracts/v1/ e docs/implementation/roadmap.md. Tipos C# locais, sem dependência de projetos irmãos.

## Repositório e solução completa

Código deste serviço: [GitHub](https://github.com/alessandroibes/fiap-cloud-games-payments). Para executar os quatro serviços juntos, siga o [README da infraestrutura](https://github.com/alessandroibes/fiap-cloud-games-infrastructure), com clones irmãos e `scripts/Start-Local.ps1` ou `scripts/Start-Kubernetes.ps1`.

Os comandos abaixo descrevem o modo de desenvolvimento individual. Não iniciar o Compose individual ao mesmo tempo que a API correspondente do Compose central, pois compartilham portas e dependências. As suítes de teste usam ambientes exclusivos.

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

Regressão concluída com 45 testes aprovados; [evidências e limites](docs/quality/g8-validation.md). Recuperação integrada no Kubernetes registrada na infraestrutura. Preparação documental da entrega G9 na infraestrutura; vídeo e relatório acadêmico dependem dos dados e da gravação do responsável.

## Variáveis de ambiente

Exemplo sem credenciais reais: [.env.example](.env.example). Gere a configuração com Initialize-Local; segredos efetivos ficam em .env ignorado. A infraestrutura central possui configuração própria e traduz suas variáveis para estas chaves da aplicação.

| Chave | Finalidade |
|---|---|
| ConnectionStrings__Payments | Conexão PostgreSQL exclusiva deste serviço |
| PAYMENTS_DOCKER_CONNECTION | Conexão usada pelo Compose individual, com DNS postgres |
| RabbitMq__Host / RabbitMq__Port | Endereço e porta AMQP |
| RabbitMq__VirtualHost / RabbitMq__Username / RabbitMq__Password | Vhost e credencial externa do broker |
| ASPNETCORE_URLS | Endereço HTTP na execução local pelo SDK |
| PaymentSimulation__Outcome | Approved ou Rejected; resultado de pagamentos já persistidos não muda |

Portas e credenciais exclusivas de testes estão descritas em .env.example e [operação](docs/operations.md). Consulte [k8s/README.md](k8s/README.md) para ConfigMap/Secret, probes e Jobs. Nunca usar credenciais de outro banco nem executar migrations automaticamente no startup.
