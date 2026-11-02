param(
    [string]$UsersEnv = (Join-Path $PSScriptRoot '../../fiap-cloud-games-users/.env'),
    [string]$UsersUrl = 'http://127.0.0.1:5081',
    [string]$CatalogUrl = 'http://127.0.0.1:5082',
    [string]$PaymentsUrl = 'http://127.0.0.1:5083'
)
$ErrorActionPreference = 'Stop'
$previousOutcome = [Environment]::GetEnvironmentVariable('PaymentSimulation__Outcome', 'Process')
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
function Start-PaymentsMode([string]$mode) {
    [Environment]::SetEnvironmentVariable('PaymentSimulation__Outcome', $mode, 'Process')
    docker compose up -d --force-recreate payments
    if ($LASTEXITCODE -ne 0) { throw 'Recriação do Payments falhou.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        try { $health = Invoke-WebRequest "$PaymentsUrl/health/ready" -UseBasicParsing; if ($health.StatusCode -eq 200) { return } } catch { }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Payments não ficou disponível.'
}
function New-DemoGame([string]$name) {
    $response = Invoke-WebRequest "$CatalogUrl/api/games" -Headers $headers -Method Post -ContentType 'application/json' -Body (@{name=$name;price=29.90} | ConvertTo-Json) -UseBasicParsing
    if ($response.StatusCode -ne 201) { throw 'Criação do jogo não retornou 201.' }
    return ($response.Content | ConvertFrom-Json).id
}
function New-DemoOrder([string]$game) {
    $response = Invoke-WebRequest "$CatalogUrl/api/orders" -Headers $headers -Method Post -ContentType 'application/json' -Body (@{gameId=$game} | ConvertTo-Json) -UseBasicParsing
    $order = $response.Content | ConvertFrom-Json
    if ($response.StatusCode -ne 202 -or $order.status -ne 'Pending') { throw 'Compra não retornou 202/Pending.' }
    return $order.orderId
}
function Wait-Order([string]$id, [string]$expected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $order = Invoke-RestMethod "$CatalogUrl/api/orders/$id" -Headers $headers
        if ($order.status -eq $expected) { return }
        if ($order.status -ne 'Pending') { throw 'Resultado terminal divergente.' }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Pedido não atingiu o resultado esperado.'
}
try {
    if (-not (Test-Path .env)) { throw 'Prepare .env e execute a migration Payments antes.' }
    $settings = @{}
    Get-Content -LiteralPath $UsersEnv | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { $pair=$_.Split('=',2); $settings[$pair[0]]=$pair[1] }
    $login = Invoke-RestMethod "$UsersUrl/api/auth/login" -Method Post -ContentType 'application/json' -Body (@{email=$settings['AdminBootstrap__Email'];password=$settings['AdminBootstrap__Password']} | ConvertTo-Json)
    $headers = @{Authorization="Bearer $($login.accessToken)"}
    $runId = [Guid]::NewGuid().ToString('N').Substring(0,8)
    Start-PaymentsMode 'Approved'
    $approvedGame = New-DemoGame "G4 aprovado $runId"
    $approvedOrder = New-DemoOrder $approvedGame
    Wait-Order $approvedOrder 'Approved'
    $library = Invoke-RestMethod "$CatalogUrl/api/library" -Headers $headers
    if (@($library | Where-Object { $_.gameId -eq $approvedGame }).Count -ne 1) { throw 'Approved não concedeu exatamente uma entrada lógica.' }
    Start-PaymentsMode 'Rejected'
    $rejectedGame = New-DemoGame "G4 rejeitado e nova tentativa $runId"
    $rejectedOrder = New-DemoOrder $rejectedGame
    Wait-Order $rejectedOrder 'Rejected'
    $library = Invoke-RestMethod "$CatalogUrl/api/library" -Headers $headers
    if (@($library | Where-Object { $_.gameId -eq $rejectedGame }).Count -ne 0) { throw 'Rejected concedeu indevidamente.' }
    Start-PaymentsMode 'Approved'
    $retryOrder = New-DemoOrder $rejectedGame
    if ($retryOrder -eq $rejectedOrder) { throw 'Nova tentativa reutilizou pedido rejeitado.' }
    Wait-Order $retryOrder 'Approved'
    Wait-Order $rejectedOrder 'Rejected'
    $library = Invoke-RestMethod "$CatalogUrl/api/library" -Headers $headers
    if (@($library | Where-Object { $_.gameId -eq $rejectedGame }).Count -ne 1) { throw 'Nova tentativa não concedeu exatamente uma entrada lógica.' }
    [pscustomobject]@{ApprovedOrder=$approvedOrder;RejectedOrder=$rejectedOrder;RetryApprovedOrder=$retryOrder;ApprovedGranted=$true;RejectedGranted=$false;RetryGrantedOnce=$true} | ConvertTo-Json
} finally {
    [Environment]::SetEnvironmentVariable('PaymentSimulation__Outcome', $previousOutcome, 'Process')
    docker compose up -d --force-recreate payments
    $restoreCode = $LASTEXITCODE
    Pop-Location
    if ($restoreCode -ne 0) { throw 'Falha ao restaurar Payments; confira o modo de simulação local.' }
}
