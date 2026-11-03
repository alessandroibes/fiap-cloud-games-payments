# G8 — Resiliência e regressão

Executado em 03/11/2026 (America/Sao_Paulo), entre 14:24:07–14:25:03, com PostgreSQL/RabbitMQ reais e exclusivos dos testes deste repositório.

- `scripts/Validate-G4.ps1`: restore locked e build concluídos, sem erros/avisos; **45 testes aprovados**, zero falhas/ignorados.
- TRX local ignorado: `tests/FiapCloudGames.Payments.Tests/TestResults/g4-tests.trx`. A execução atual substituiu o arquivo local anterior; o registro histórico do gate original foi preservado.
- Código preservado; reexecutados os casos de concorrência, rollback, falha após commit, Outbox com broker indisponível, retries limitados, erro e replay.

No Kubernetes, o roteiro Validate-G8 da infraestrutura comprovou cadastro e compras durante queda do broker, Outboxes preservadas após reinício de Users/Catalog, retomada com um pagamento/concessão/confirmação, reenvios sem efeitos duplicados e mensagem inválida na fila de erro sem efeito parcial. O evento inválido ficou preservado como evidência; não há purge/replay automático de mensagens inválidas.

Evidências integradas e identificadores: `fiap-cloud-games-infrastructure/docs/quality/g8-validation.md`. Procedimento de recuperação: `docs/implementation/recovery-runbook.md` no mesmo repositório. As 171 aprovações do conjunto incluem os testes deste serviço, não são uma suíte adicional.

Não foram testados perda do nó, recuperação de backup ou carga prolongada. Falhas precisas após commit são injetadas em testes; reinícios de pods e indisponibilidade do broker também foram executados no cluster. Sem garantia de exactly-once físico no console. Fase 1 intacta; nenhum push/publicação externa.