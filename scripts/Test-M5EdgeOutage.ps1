[CmdletBinding()]
param([ValidateRange(1, 2400)][int]$OutageSeconds = 1800)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$reviewProject = 'telemetrix-m5-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$producerName = "$reviewProject-source"
$producerStarted = $false
$composeFile = Join-Path $repoRoot 'deploy/docker-compose.m5.yml'
# M5 reuses the disposable M4 infrastructure declaration, never deploy/.env.
$env:M4_POSTGRES_PASSWORD = [Guid]::NewGuid().ToString('N')
$env:M4_RUN_ID = [Guid]::NewGuid().ToString('D')
$env:M5_TICKS = [string]($OutageSeconds + 40)

function Invoke-ReviewCompose {
    & docker compose -p $reviewProject -f $composeFile @args
    if ($LASTEXITCODE -ne 0) { throw 'M5 review Compose command failed.' }
}

function Get-SourceLog {
    # Windows PowerShell 5 treats redirected native stderr as ErrorRecords.
    # Broker-disconnect diagnostics are expected during this test; exit status
    # still determines whether reading the logs itself failed.
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $entries = & docker logs $producerName 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'Cannot read M5 source logs.' }
        $entries | ForEach-Object { $_.ToString() }
    }
    finally { $ErrorActionPreference = $previousPreference }
}

Push-Location $repoRoot
try {
    Write-Host "Isolated M5 project: $reviewProject; outage=$OutageSeconds seconds"
    Invoke-ReviewCompose build
    Invoke-ReviewCompose up -d --wait kafka timescaledb
    Invoke-ReviewCompose run --rm probe prepare
    Invoke-ReviewCompose up -d transformer
    Invoke-ReviewCompose run -d --name $producerName probe edge-send
    $producerStarted = $true
    $readyDeadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do {
        $ready = (Get-SourceLog | Select-String -SimpleMatch 'M5_SOURCE_READY')
        if ([DateTimeOffset]::UtcNow -gt $readyDeadline) { throw 'Default-load source did not become ready.' }
        if (-not $ready) { Start-Sleep -Seconds 1 }
    } while (-not $ready)
    Write-Host 'M5 source ready: 7 samples/s, default 500 samples / 5 s batching'
    Invoke-ReviewCompose stop kafka
    $outage = [Diagnostics.Stopwatch]::StartNew()
    $nextProgress = 0
    while ($outage.Elapsed.TotalSeconds -lt $OutageSeconds) {
        $running = & docker inspect --format '{{.State.Running}}' $producerName
        if ($LASTEXITCODE -ne 0 -or $running -ne 'true') { throw 'Source stopped during Kafka outage.' }
        if ($outage.Elapsed.TotalSeconds -ge $nextProgress) {
            Write-Host "Kafka remains stopped; elapsed=$([int]$outage.Elapsed.TotalSeconds) s"
            $nextProgress += 60
        }
        Start-Sleep -Seconds 1
    }
    Write-Host "Measured Kafka outage: $($outage.Elapsed.TotalSeconds.ToString('F3')) s"
    Invoke-ReviewCompose up -d --wait kafka
    $producerExit = & docker wait $producerName
    if ($LASTEXITCODE -ne 0 -or $producerExit -ne '0') { throw 'Default-load source failed.' }
    Get-SourceLog | Select-String -Pattern 'M5_|buffering started|buffer is empty'
    Invoke-ReviewCompose run --rm probe edge-verify
}
finally {
    if ($reviewProject -notmatch '^telemetrix-m5-[0-9a-f]{12}$') { throw 'Refusing cleanup of an unexpected project.' }
    if ($producerStarted) { & docker rm -f $producerName | Out-Null }
    & docker compose -p $reviewProject -f $composeFile down -v --remove-orphans --rmi local
    $cleanupExit = $LASTEXITCODE
    Pop-Location
    Remove-Item Env:M4_POSTGRES_PASSWORD, Env:M4_RUN_ID, Env:M5_TICKS -ErrorAction SilentlyContinue
    if ($cleanupExit -ne 0) { throw 'M5 review resource cleanup failed.' }
}
Write-Host 'M5 outage review passed; isolated resources cleaned up.'
