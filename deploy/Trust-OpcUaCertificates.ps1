param(
    [ValidateSet('plantsim', 'opc-plc')][string]$Server = 'plantsim',
    [string]$ProjectName = 'telemetrix',
    [string]$BridgeThumbprint,
    [string]$ServerThumbprint
)
$ErrorActionPreference = 'Stop'
$compose = Join-Path $PSScriptRoot 'docker-compose.yml'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('telemetrix-public-certs-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    foreach ($service in @('bridge', $Server)) {
        $target = Join-Path $scratch $service
        New-Item -ItemType Directory -Path $target | Out-Null
        # Public certificates only. Never copy own/private or the entire PKI root.
        & docker compose -f $compose -p $ProjectName cp "${service}:/app/pki/own/certs/." $target
        if ($LASTEXITCODE -ne 0) { throw "Cannot copy $service public certificate." }
        $files = @(Get-ChildItem -LiteralPath $target -File -Filter '*.der')
        if ($files.Count -ne 1) { throw "Expected exactly one $service application certificate." }
        $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($files[0].FullName)
        try {
            Write-Host "$service subject=$($certificate.Subject) thumbprint=$($certificate.Thumbprint)"
            $expected = if ($service -eq 'bridge') { $BridgeThumbprint } else { $ServerThumbprint }
            if (!$expected) { continue }
            if ($certificate.Thumbprint -ne $expected.Replace(' ', '')) { throw "$service thumbprint mismatch; no trust granted." }
        } finally { $certificate.Dispose() }
    }
    if (!$BridgeThumbprint -or !$ServerThumbprint) {
        Write-Host 'Inspect both identities, then rerun with -BridgeThumbprint and -ServerThumbprint. No trust granted.'
        return
    }
    foreach ($pair in @(@('bridge', $Server), @($Server, 'bridge'))) {
        $from = $pair[0]; $to = $pair[1]
        $file = Get-ChildItem -LiteralPath (Join-Path $scratch $from) -File -Filter '*.der'
        & docker compose -f $compose -p $ProjectName exec -T $to mkdir -p /app/pki/trusted/certs
        if ($LASTEXITCODE -ne 0) { throw "Cannot open $to trusted store." }
        & docker compose -f $compose -p $ProjectName cp $file.FullName "${to}:/app/pki/trusted/certs/$($file.Name)"
        if ($LASTEXITCODE -ne 0) { throw "Cannot trust $from certificate on $to." }
    }
    Write-Host 'Explicit mutual trust granted. Bridge retries automatically; neither service was restarted.'
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        !(Split-Path $resolved -Leaf).StartsWith('telemetrix-public-certs-')) { throw 'Unexpected cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
