param([switch]$Docker)
$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('telemetrix-secret-scan-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../.gitleaks.toml') -Destination $scratch
    & git -C $scratch init -b fake-private-key --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated fixture branch.' }
    # Deliberately invalid fixture, never an actual private key. Assemble the
    # header so this regression-test source itself is not a key fixture.
    $marker = '-' * 5
    $fixture = "$marker`BEGIN RSA PRIVATE KEY$marker`nTHIS_IS_A_FAKE_TEST_KEY_WITH_NO_CRYPTOGRAPHIC_MATERIAL`n$marker`END RSA PRIVATE KEY$marker"
    [IO.File]::WriteAllText((Join-Path $scratch 'fake-key.pem'), $fixture)
    & git -C $scratch add .
    & git -C $scratch -c user.name=Telemetrix -c user.email=fixture@telemetrix.invalid commit --quiet -m 'test: commit deliberately invalid key fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot commit isolated key fixture.' }
    if ($Docker) {
        & docker run --rm -v "${scratch}:/repo:ro" zricethezav/gitleaks:v8.30.1 git --redact --config /repo/.gitleaks.toml /repo
    } else {
        & gitleaks git --redact --config (Join-Path $scratch '.gitleaks.toml') $scratch
    }
    if ($LASTEXITCODE -ne 1) { throw "Expected secret scanner exit 1; received $LASTEXITCODE." }
    Write-Host 'M9_SECRET_NEGATIVE_PASS: committed fake private key rejected on isolated fixture branch.'
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        !(Split-Path $resolved -Leaf).StartsWith('telemetrix-secret-scan-')) { throw 'Unexpected cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
