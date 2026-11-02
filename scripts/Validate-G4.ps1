$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if (-not (Test-Path .env)) { throw 'Execute scripts/Initialize-Local.ps1 antes.' }
    docker compose --env-file .env -f tests/compose.yaml config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Compose de testes inválido.' }
    docker compose --env-file .env -f tests/compose.yaml up -d --wait --wait-timeout 180
    if ($LASTEXITCODE -ne 0) { throw 'Dependências de teste indisponíveis.' }
    dotnet restore FiapCloudGames.Payments.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore falhou.' }
    dotnet build FiapCloudGames.Payments.sln --no-restore -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Build falhou.' }
    dotnet test FiapCloudGames.Payments.sln --no-build --logger 'trx;LogFileName=g4-tests.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Testes falharam.' }
} finally { Pop-Location }
