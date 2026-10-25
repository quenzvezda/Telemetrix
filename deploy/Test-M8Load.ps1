param(
    [ValidateSet('latency', 'throughput')][string] $Kind = 'latency',
    [ValidateRange(1, 100000)][int] $MaxSamples = 500,
    [ValidateRange(1, 60)][int] $BatchSeconds = 5,
    [ValidateRange(1, 4096)][int] $BatchQueueCapacity,
    [ValidateRange(10, 7200)][int] $Seconds,
    [string] $Report,
    [string] $Runner
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if (-not $PSBoundParameters.ContainsKey('Seconds')) {
    $Seconds = if ($Kind -eq 'latency') { 1800 } else { 900 }
}
if (-not $PSBoundParameters.ContainsKey('BatchQueueCapacity')) {
    $BatchQueueCapacity = if ($Kind -eq 'throughput') { 1024 } else { 64 }
}
if (-not $Report) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $Report = Join-Path $taskRoot "artifacts/m8-$Kind-$MaxSamples-$BatchSeconds-$stamp.json"
}
Write-Host "M8 $Kind profile: $Seconds seconds, batching $MaxSamples / $BatchSeconds seconds"
Write-Host "Disposable Kafka/TimescaleDB/source containers; the existing Compose database is not used."
if ($Seconds -lt $(if ($Kind -eq 'latency') { 1800 } else { 900 })) {
    Write-Host 'SMOKE RUN: this duration cannot satisfy the milestone acceptance duration.'
}
$names = 'M8_LOAD_KIND', 'M8_LOAD_SECONDS', 'M8_BATCH_SAMPLES', 'M8_BATCH_SECONDS', 'M8_BATCH_QUEUE_CAPACITY', 'M8_LOAD_REPORT'
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:M8_LOAD_KIND = $Kind
    $env:M8_LOAD_SECONDS = "$Seconds"
    $env:M8_BATCH_SAMPLES = "$MaxSamples"
    $env:M8_BATCH_SECONDS = "$BatchSeconds"
    $env:M8_BATCH_QUEUE_CAPACITY = "$BatchQueueCapacity"
    $env:M8_LOAD_REPORT = [IO.Path]::GetFullPath($Report)
    Push-Location $taskRoot
    try {
        if ($Runner) {
            dotnet $Runner -class Telemetrix.Integration.Tests.LoadProfileTests -explicit only -showLiveOutput
        } else {
            dotnet run --project tests/Telemetrix.Integration.Tests -c Release -- -class Telemetrix.Integration.Tests.LoadProfileTests -explicit only -showLiveOutput
        }
        if ($LASTEXITCODE -ne 0) { throw "M8 load profile failed. Inspect $Report" }
    } finally { Pop-Location }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}
