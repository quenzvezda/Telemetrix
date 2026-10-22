[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$reviewProject = 'telemetrix-m4-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$producerName = "$reviewProject-load"
$producerStarted = $false
$composeFile = Join-Path $repoRoot 'deploy/docker-compose.m4.yml'
$env:M4_POSTGRES_PASSWORD = [Guid]::NewGuid().ToString('N')
$env:M4_RUN_ID = [Guid]::NewGuid().ToString('D')
$env:M4_BATCHES = '2000'
$env:M4_SAMPLES = '10'
$env:M4_DELAY_MS = '10'

function Invoke-ReviewCompose {
    & docker compose -p $reviewProject -f $composeFile @args
    if ($LASTEXITCODE -ne 0) { throw 'Review Compose command failed.' }
}

Push-Location $repoRoot
try {
    Write-Host "Isolated project: $reviewProject"
    Invoke-ReviewCompose build
    Invoke-ReviewCompose up -d --wait kafka timescaledb
    Invoke-ReviewCompose run --rm probe prepare
    Invoke-ReviewCompose up -d transformer
    Invoke-ReviewCompose run -d --name $producerName probe send
    $producerStarted = $true

    $killDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        $count = [int](Invoke-ReviewCompose run --rm probe count | Select-Object -Last 1)
        if ([DateTimeOffset]::UtcNow -gt $killDeadline) { throw 'Transformer did not start storing before the deadline.' }
        if ($count -eq 0) { Start-Sleep -Milliseconds 200 }
    } while ($count -eq 0)
    $running = & docker inspect --format '{{.State.Running}}' $producerName
    if ($LASTEXITCODE -ne 0 -or $running -ne 'true' -or $count -ge 20000) { throw 'Load finished before the kill; no mid-stream proof.' }
    Write-Host "Measurements before SIGKILL: $count; source still running"
    Invoke-ReviewCompose kill -s SIGKILL transformer
    $transformerId = Invoke-ReviewCompose ps -a -q transformer
    $exitCode = & docker inspect --format '{{.State.ExitCode}}' $transformerId
    if ($LASTEXITCODE -ne 0 -or $exitCode -ne '137') { throw 'Expected transformer exit 137 after SIGKILL.' }
    Write-Host 'Transformer exit code after SIGKILL: 137'
    Invoke-ReviewCompose start transformer
    $producerExit = & docker wait $producerName
    if ($LASTEXITCODE -ne 0 -or $producerExit -ne '0') { throw 'Deterministic source failed.' }
    & docker logs $producerName
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read source result.' }
    Invoke-ReviewCompose run --rm probe verify
}
finally {
    # Only this invocation's newly created project and volume are disposable.
    if ($reviewProject -notmatch '^telemetrix-m4-[0-9a-f]{12}$') { throw 'Refusing cleanup of an unexpected project.' }
    if ($producerStarted) { & docker rm -f $producerName | Out-Null }
    & docker compose -p $reviewProject -f $composeFile down -v --remove-orphans --rmi local
    $cleanupExit = $LASTEXITCODE
    Pop-Location
    Remove-Item Env:M4_POSTGRES_PASSWORD, Env:M4_RUN_ID, Env:M4_BATCHES, Env:M4_SAMPLES, Env:M4_DELAY_MS -ErrorAction SilentlyContinue
    if ($cleanupExit -ne 0) { throw 'Review resource cleanup failed.' }
}
Write-Host 'Review completed successfully; isolated resources cleaned up.'
