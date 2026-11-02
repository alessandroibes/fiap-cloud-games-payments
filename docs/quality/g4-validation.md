# Evidências do G4 — PaymentsAPI

Validação local em 02/11/2026, Windows/PowerShell, SDK .NET 8 e Docker Desktop Linux. Código/documentação alterados somente em Payments. Repositórios irmãos e Fase 1 permaneceram sem alterações de arquivos. Dados demonstrativos foram criados pelo fluxo HTTP/eventos autorizado, sem leitura entre bancos pela aplicação.

## Comandos e resultados

| Verificação | Resultado observado |
|---|---|
| dotnet restore FiapCloudGames.Payments.sln --locked-mode | Sucesso sem avisos na execução final |
| dotnet build FiapCloudGames.Payments.sln --no-restore -m:1 | Sucesso, zero avisos e erros |
| dotnet test FiapCloudGames.Payments.sln --no-build --logger 'trx;LogFileName=g4-tests.trx' | 45 executados, 45 aprovados, zero falhas/ignorados; execução final de 15:58:39 a 16:02:19, horário de Brasília |
| dotnet format whitespace FiapCloudGames.Payments.sln --verify-no-changes --no-restore | Código de saída zero |
| dotnet ef migrations has-pending-model-changes, Infrastructure/Api, --no-build | Nenhuma alteração pendente no modelo |
| docker compose config --quiet e docker compose build payments | Sucesso; restore locked e publish Release no Dockerfile |
| docker compose run --rm payments --migrate, duas execuções | Ambas concluídas; uma migration em fcg_payments |
| Container Payments | Em execução, UID 1654, porta 5083 somente no localhost |
| GET /health/live e /health/ready | HTTP 200 |
| scripts/Validate-LocalFlow.ps1 | Approved concedeu; Rejected não concedeu; nova tentativa criou outro pedido e concedeu uma entrada |
| Modo após demonstração | Approved restaurado |
| Persistência final local | Payments: três Approved, um Rejected; quatro receipts; zero OutboxMessage; quatro InboxState consumidos e entregues |
| Catalog após demonstração | Três pedidos Approved, um Rejected e três entradas na biblioteca |
| Filas | payments-order-placed-v1 e catalog-payment-processed-v1 vazias, cada uma com um consumer; notifications-payment-processed-v1 com quatro mensagens aguardando G5 |

Imagem validada: `sha256:29483411ac2202051359df94f5402c96c1e4b65a477e09b164961cf82b256413`. Build local, sem publicação em registry. Contagens refletem o instante da captura e podem mudar com uso posterior.

## Demonstração com serviços reais

JWT emitido pela UsersAPI foi usado para criar jogos/pedidos no Catalog; Payments consumiu pedidos e Catalog consumiu resultados reais. Identificadores da execução:

| Cenário | OrderId | Verificação |
|---|---|---|
| Aprovado | c2291bf7-e17c-4f92-90d4-528399acc554 | Approved e jogo concedido |
| Rejeitado | 91e61bc0-6101-47d4-999c-0db543489655 | Rejected e jogo ausente da biblioteca |
| Nova tentativa do jogo rejeitado | 455d4c68-2ab4-47e0-93fb-29715ce8e040 | Novo OrderId, Approved, uma concessão; pedido anterior permaneceu Rejected |

O pedido pendente demonstrativo do G3 também foi consumido após implantação de Payments, explicando o quarto pagamento e a terceira concessão nas contagens finais. Notifications ainda não estava implantada: mensagens retidas não comprovam notificação enviada. O G5 deverá consumir Approved para confirmação e ignorar Rejected nesse fluxo.

## Cobertura automatizada

- Domínio: ambos os resultados determinísticos, padrão Approved, preço zero, modos inválidos, GUIDs, UTC, BRL, escala e precisão monetária.
- Contratos: tipos locais e assembly independente, campos obrigatórios/canônicos, JSON camelCase, decimal numérico, UTC e identidade MassTransit V1; transporte real por RabbitMQ.
- Dois observers independentes recebem o mesmo PaymentProcessedEvent Approved/Rejected com IDs e snapshot persistidos.
- Duplicatas concorrentes com MessageId diferente e EventId novo para o mesmo pedido geram um pagamento e um resultado lógico.
- Replay do mesmo MessageId e mudança de modo após reinício não recalculam resultado já persistido.
- EventId com conteúdo divergente e OrderId com snapshot conflitante seguem para _error sem novo resultado; 59.9 e 59.90 são equivalentes no fingerprint.
- Mensagens inválidas não deixam pagamento/receipt nem resultado publicado.
- Falha ao salvar Outbox após salvar Payment/Receipt na transação reverte o conjunto; falha transitória retenta e conclui.
- Quatro tentativas esgotadas deixam estado de negócio/Outbox ausente, encaminham a _error e permitem recuperação por replay após corrigir a causa.
- Falha injetada depois do commit e antes de entrega/ACK recupera sem duplicar pagamento ou receipt.
- Broker interrompido depois do commit: pagamento, receipt e Outbox duráveis foram observados antes da entrega; após reiniciar API/broker, ambos os observers receberam o resultado e a Outbox esvaziou.
- Filas de destino recriadas retêm resultado até consumidores tardios iniciarem, sem competir por uma fila compartilhada.
- Índice único PostgreSQL impede duplicação de OrderId mesmo sem a lógica da aplicação.
- HTTP expõe health checks, sem endpoint de pagamento; schema vazio permanece vazio no startup normal e readiness retorna 503.

## Correções verificadas

A primeira execução detectou diferença entre ticks .NET e microssegundos PostgreSQL. O instante do resultado foi normalizado à precisão persistível e passou a coincidir exatamente entre banco e evento.

Os testes também detectaram uma corrida na fixture ao descartar hosts antes de o bus concluir a inicialização. A fixture passou a aguardar startup nos reinícios normais; o cenário de broker indisponível mantém início sem espera. Após essas correções, os dois testes focados passaram e a suíte completa final aprovou os 45 casos. A primeira execução interrompida e as falhas intermediárias não são contadas como sucesso.

## Reprodução e limites

Executar Initialize-Local.ps1 e Validate-G4.ps1 conforme [operação](../operations.md). TRX fica em TestResults ignorado; não versionar .env, tokens ou cookies. A fixture usa banco/broker exclusivos de testes e consome sua _error para observar roteamento. Produção não consome automaticamente a fila de erro.

O roteiro Validate-LocalFlow.ps1 usa containers reais de Users/Catalog/Payments, cria dados demonstrativos e muda temporariamente o modo global de Payments; não executar simultaneamente com outras compras locais. Não remove dados ao terminar e restaura a configuração anterior.

Não foi validado envio de notificações reais, Compose centralizado com quatro serviços, Kubernetes, carga ou múltiplas réplicas de Payments. Esses itens permanecem nos gates seguintes. A garantia continua at-least-once com idempotência lógica, sem promessa de entrega física exatamente uma vez.
