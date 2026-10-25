param([ValidateRange(10, 600)][int]$Seconds = 600)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$previousSeconds = $env:TELEMETRIX_M7_COMPARE_SECONDS
Push-Location $repository
try {
    dotnet build tests/Telemetrix.Integration.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Integration test build failed.' }
    [System.IO.Directory]::CreateDirectory((Join-Path $repository 'artifacts')) | Out-Null
    $env:TELEMETRIX_M7_COMPARE_SECONDS = $Seconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    dotnet tests/Telemetrix.Integration.Tests/bin/Release/net10.0/Telemetrix.Integration.Tests.dll `
        -class Telemetrix.Integration.Tests.MqttComparisonTests -showLiveOutput -noColor `
        -result-xml artifacts/m7-comparison.xml
    if ($LASTEXITCODE -ne 0) { throw 'M7 protocol comparison failed.' }
} finally {
    $env:TELEMETRIX_M7_COMPARE_SECONDS = $previousSeconds
    Pop-Location
}
