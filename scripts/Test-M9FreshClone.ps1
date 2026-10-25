#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$branch = & git -C $repository branch --show-current
if ($LASTEXITCODE -ne 0 -or !$branch) { throw 'A committed branch is required.' }
$identity = [guid]::NewGuid().ToString('N')
$project = 'telemetrix-m9-fresh-' + $identity.Substring(0, 12)
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('telemetrix-m9-fresh-' + $identity)
$compose = Join-Path $scratch 'deploy/docker-compose.yml'
$override = Join-Path $scratch 'deploy/fresh-check.yml'
function Compose {
    & docker compose --progress plain -f $compose -f $override -p $project @args
    if ($LASTEXITCODE -ne 0) { throw "Fresh-clone compose command failed: $($args[0])" }
}
try {
    & git clone --quiet --no-hardlinks --single-branch --branch $branch $repository $scratch
    if ($LASTEXITCODE -ne 0) { throw 'Cannot clone the committed branch.' }
    # Isolated test credentials, supplied only through an ignored dotenv file.
    [IO.File]::WriteAllText((Join-Path $scratch 'deploy/.env'), "POSTGRES_PASSWORD=$([guid]::NewGuid().ToString('N'))`n")
    $services = @('bridge','transformer','plantsim','opc-plc','mosquitto','kafka','kafka-ui','timescaledb','grafana','prometheus','mqttsim')
    $configuration = "services:`n" + (($services | ForEach-Object { "  ${_}:`n    ports: !reset []" }) -join "`n")
    [IO.File]::WriteAllText($override, $configuration)
    Compose --profile secure config --quiet
    Compose --profile secure up -d --build
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    $public = Join-Path $scratch 'public-certificates'
    New-Item -ItemType Directory -Path $public | Out-Null
    $thumbprints = @{}
    foreach ($service in @('bridge','plantsim')) {
        $destination = Join-Path $public $service
        New-Item -ItemType Directory -Path $destination | Out-Null
        do {
            & docker compose -f $compose -p $project cp "${service}:/app/pki/own/certs/." $destination 2>$null
            $files = @(Get-ChildItem -LiteralPath $destination -File -Filter '*.der')
            if ($files.Count -eq 1) { break }
            if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Certificate provisioning timed out.' }
            Start-Sleep -Seconds 1
        } while ($true)
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($files[0].FullName)
        try { $thumbprints[$service] = $certificate.Thumbprint } finally { $certificate.Dispose() }
    }
    & (Join-Path $scratch 'deploy/Trust-OpcUaCertificates.ps1') -ProjectName $project -BridgeThumbprint $thumbprints.bridge -ServerThumbprint $thumbprints.plantsim
    do {
        $count = Compose exec -T timescaledb psql -X -v ON_ERROR_STOP=1 -U telemetry -d telemetry -Atc 'SELECT count(*) FROM measurement'
        if ([long]$count -gt 0) { break }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Fresh-clone measurements timed out.' }
        Start-Sleep -Seconds 1
    } while ($true)
    $logs = Compose logs --no-log-prefix bridge
    if (!($logs -match 'session established with SignAndEncrypt')) { throw 'No secured session log.' }
    Compose ps --all
    Write-Host "M9_FRESH_CLONE_PASS: secure session; measurements=$count; secrets supplied through ignored dotenv; isolated project=$project"
} finally {
    if (Test-Path -LiteralPath $override) {
        if ($project -notmatch '^telemetrix-m9-fresh-[a-f0-9]{12}$') { throw 'Unexpected Docker cleanup target.' }
        & docker compose -f $compose -f $override -p $project --profile secure down --volumes --rmi local --remove-orphans
    }
    if (Test-Path -LiteralPath $scratch) {
        $resolved = [IO.Path]::GetFullPath($scratch)
        $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (!$resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            !(Split-Path $resolved -Leaf).StartsWith('telemetrix-m9-fresh-')) { throw 'Unexpected clone cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
