# Operação local — PaymentsAPI

## Preparação

Pré-requisitos: SDK .NET 8.0.414 ou patch compatível, Docker Desktop Linux e infraestrutura G1 em execução. Não importar dados da Fase 1. Na raiz deste repositório:

```powershell
./scripts/Initialize-Local.ps1
docker compose config --quiet
docker compose build payments
docker compose run --rm payments --migrate
docker compose up -d payments
```

Initialize-Local lê apenas a configuração local de Infraestrutura para preparar a credencial exclusiva fcg_payments e a conexão RabbitMQ. Caminho alternativo: -InfrastructureEnv. Segredos de testes são gerados localmente; .env existente é preservado. A aplicação não lê arquivos ou bancos dos serviços irmãos.

Compose utiliza a rede externa fiap-cloud-games_default criada no G1. Imagem local fiap-cloud-games-payments:local, sem publicação automática. Dockerfile multi-stage, bases fixadas por digest, runtime não privilegiado. Porta 5083 publicada somente no localhost. `docker compose stop payments` para o serviço sem remover dados.

Alternativa pelo SDK:

```powershell
./scripts/Invoke-Payments.ps1 -Action migrate
./scripts/Invoke-Payments.ps1 -Action run
```

O script importa .env apenas no processo e restaura o ambiente ao terminar. Caso a política local bloqueie scripts, usar -ExecutionPolicy Bypass somente no processo, sem alterar a política global.

## Configuração e HTTP

| Chave | Uso |
|---|---|
| ConnectionStrings__Payments | PostgreSQL exclusivo fcg_payments; localhost:55432 |
| PAYMENTS_DOCKER_CONNECTION | Mesmo banco/credencial via postgres:5432 no Compose |
| PaymentSimulation__Outcome | Approved (padrão) ou Rejected, com essa grafia exata |
| RabbitMq__Host / Port / VirtualHost | Broker externo; no Compose rabbitmq:5672, vhost fcg |
| RabbitMq__Username / Password | Credenciais externas; não usar guest |
| TEST_DB_PASSWORD / TEST_RABBIT_PASSWORD | Segredos exclusivos dos containers de teste |
| TEST_DB_PORT / TEST_RABBIT_PORT | 55435 / 5675 por padrão |
| ASPNETCORE_URLS | Endereço SDK; container usa 8080 |

Payments expõe somente GET /health/live (200) e GET /health/ready (200 se banco/schema disponíveis, 503 caso contrário). Readiness não mede conectividade do broker, backlog ou conclusão dos pagamentos; observar logs/filas também. Não há endpoint HTTP para pagar, alterar resultado ou consultar dados pessoais. A consulta do pedido permanece no Catalog, protegida pelo JWT de Users. Payments não necessita da chave JWT para seus health checks e consumo de mensagens.

Nunca versionar .env, credenciais ou cookies Erlang. `docker compose config` sem --quiet expande segredos; não usar sua saída como evidência compartilhada. Logs JSON usam identificadores técnicos e resultado, sem payload pessoal ou string de conexão. Acesso de publicação ao broker deve ser restrito aos serviços autorizados; uma mensagem não é autenticada pelo JWT do comprador.

## Simulação determinística

A configuração do processo decide o resultado de novos pedidos, sem sorteio e sem parâmetro controlável pelo comprador. Valores diferentes de Approved/Rejected impedem startup. O modo é lido ao iniciar; após alterar configuração, recriar/reiniciar o serviço.

O primeiro pagamento confirmado no banco por OrderId é definitivo. Reentrega equivalente não recalcula o resultado, mesmo após troca de modo. Rejected não significa falha técnica: gera PaymentProcessedEvent Rejected normalmente. Falhas de banco/broker não se transformam em rejeição comercial. Um novo pedido após rejeição possui outro OrderId e será processado conforme o modo então vigente.

## Persistência e transação

PaymentsDbContext possui Payments, OrderReceipts e tabelas técnicas InboxState/OutboxMessage/OutboxState do MassTransit. OrderId, UserId e GameId são referências externas sem FK entre bancos. Payments.OrderId e ResultEventId têm índices únicos; OrderReceipts.EventId é PK. Price usa numeric(18,2), BRL e validação de duas casas. O instante do resultado é UTC com precisão de microssegundos, compatível com PostgreSQL.

Executar --migrate explicitamente antes da API. O comando aplica somente a migration InitialPaymentsAndMessaging ao banco Payments e encerra sem iniciar o bus. Startup normal não chama Migrate/EnsureCreated. A ferramenta dotnet-ef 9.0.10 está em .config/dotnet-tools.json; para novas migrations, configurar ConnectionStrings__Payments, usar Infrastructure como project e Api como startup-project, revisar e testar. Não utilizar reset/drop como atualização.

O endpoint usa Consumer Outbox do MassTransit EF. O middleware administra a transação que inclui pagamento, receipt, InboxState e PaymentProcessedEvent; o consumer publica via ConsumeContext. O store exige transação existente e não abre transação independente. A entrega ocorre após commit e a mensagem recebida só é concluída pelo transporte após o fluxo da Outbox. Referência da versão utilizada: [EntityFrameworkOutboxContextFactory no MassTransit 8.5.8](https://github.com/MassTransit/MassTransit/blob/v8.5.8/src/Persistence/MassTransit.EntityFrameworkCoreIntegration/EntityFrameworkCoreIntegration/EntityFrameworkOutboxContextFactory.cs).

ReadCommitted e advisory locks transacionais PostgreSQL por EventId e OrderId serializam duplicatas inclusive entre réplicas. OrderReceipts mantém deduplicação lógica sem depender do MessageId/janela de limpeza da Inbox técnica. Fingerprint detecta EventId reutilizado com payload divergente; preço é normalizado a duas casas para considerar 59.9 e 59.90 equivalentes. Novo EventId para o mesmo pedido com usuário/jogo/preço/moeda iguais gera receipt, sem novo pagamento ou evento de resultado. Divergência gera erro permanente. Não remover pagamentos/receipts sem planejar retenção e idempotência.

O log de resultado preparado ocorre dentro da transação e não é prova isolada de commit. Conferir estado persistido, Outbox e consumidor. Entrega é at-least-once; reentrega física pode ocorrer, mas o resultado lógico possui PaymentId/EventId persistidos e únicos.

## Filas, retentativas e recuperação

- Entrada: payments-order-placed-v1, OrderPlacedEvent V1.
- Saída: PaymentProcessedEvent V1, com binding durável para catalog-payment-processed-v1 e notifications-payment-processed-v1. DeployPublishTopology prepara ambas antes de entregar resultados, inclusive antes do primeiro consumidor.
- Erros: payments-order-placed-v1_error, sem consumidor automático de produção.

GUIDs vazios, data não UTC, preço/moeda inválidos, EventId divergente e pedido já processado com snapshot conflitante não são retentados. Falhas transitórias recebem três retentativas em 1, 5 e 15 segundos, totalizando quatro tentativas, antes de _error. Não há loop automático de reenvio de erros permanentes.

Depois de corrigir a causa de uma falha técnica, inspecionar headers/identificadores e reenviar a mensagem original, preservando EventId e snapshot. Não alterar preço/status para forçar pagamento. O replay pode ter outro MessageId, pois a deduplicação lógica permanece. Mensagens inválidas exigem investigação do produtor e do contrato.

Se o broker cair após commit, o resultado fica na Outbox. O consumer recupera a entrega com a reentrega da mensagem recebida após reconexão/reinício. Diferentemente de um produtor HTTP com Bus Outbox, a Consumer Outbox está associada à Inbox da mensagem recebida. Não apagar a fila de entrada/volumes ou purgar registros técnicos como tentativa de recuperação.

## Validação

```powershell
./scripts/Validate-G4.ps1
```

O script sobe fcg-payments-g4-tests com PostgreSQL e RabbitMQ próprios, restaura com lockfiles, compila e testa. Banco fcg_payments_test, vhost fcg_test; portas 55435/5675. Testes usam GUIDs novos e mantêm volumes. A suíte para/reinicia somente seu broker, recria filas de destino de testes e injeta falhas em interceptores EF exclusivos das fixtures. Não executar duas suítes simultâneas. A fixture consome _error somente nesse ambiente para verificar roteamento.

`dotnet test FiapCloudGames.Payments.sln --filter 'Category!=Integration'` executa domínio/contratos sem containers. TRX fica em TestResults ignorado pelo Git. Tipos dos produtores/consumidores de testes estão em assembly independente.

Com Users, Catalog e Payments locais executando e migrations aplicadas:

```powershell
./scripts/Validate-LocalFlow.ps1
```

Esse script autentica com o administrador local de Users, cria dois jogos e três pedidos demonstrativos em Catalog, muda temporariamente o modo do container Payments e verifica Approved, Rejected sem concessão e nova tentativa aprovada. Ao terminar restaura a configuração de modo anterior. Não remove os dados demonstrativos. Não executar durante outras compras locais, pois a mudança de modo afeta todos os novos pedidos consumidos nesse intervalo. Não altera arquivos/repos irmãos nem imprime JWT/credenciais.

Notifications real pertence ao G5; Compose completo ao G6; /k8s e Docker Desktop Kubernetes ao G7. .NET 8, EF Core 9.0.10, Npgsql EF 9.0.4 e MassTransit 8.5.8 mantêm versões adotadas em G1–G3, fixadas em lockfiles.
