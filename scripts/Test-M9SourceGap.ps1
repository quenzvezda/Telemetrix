param([ValidateRange(60, 300)][int]$OutageSeconds = 60)
$ErrorActionPreference = 'Stop'
$compose = Join-Path $PSScriptRoot '../deploy/docker-compose.yml'
function Compose {
    & docker compose -f $compose -p telemetrix @args
    if ($LASTEXITCODE -ne 0) { throw "Compose failed: $($args[0])" }
}
$enabled = Compose exec -T transformer printenv PlantEvents__Enabled
if ($enabled -ne 'true') { throw 'Enable PLANT_EVENTS_ENABLED=true after reviewing/applying migrations first.' }
$started = [DateTimeOffset]::UtcNow
try {
    Compose stop plantsim
    $outage = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $OutageSeconds
} finally {
    Compose up -d plantsim
}
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
$sql = "SELECT extract(epoch FROM gap_end-gap_start) FROM plant_event WHERE asset_id='Pump01' AND event_type='Gap' AND occurred_at >= '$($started.ToString('O'))' ORDER BY occurred_at DESC LIMIT 1"
do {
    $duration = Compose exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -Atc $sql
    if ($duration -and [double]::Parse($duration, [Globalization.CultureInfo]::InvariantCulture) -ge $OutageSeconds) { break }
    if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Secured source gap/recovery was not stored before the deadline.' }
    Start-Sleep -Milliseconds 500
} while ($true)
Write-Host "M9_SECURE_GAP_PASS: outageSeconds=$OutageSeconds; storedPumpGapSeconds=$duration; elapsedSeconds=$($outage.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture))"
Compose exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -c "SELECT asset_id,gap_start,gap_end FROM plant_event WHERE event_type='Gap' AND occurred_at >= '$($started.ToString('O'))' ORDER BY asset_id"
Compose logs --since $started.ToString('O') --no-log-prefix bridge | Select-String -Pattern 'session established|session restored|subscription created'
