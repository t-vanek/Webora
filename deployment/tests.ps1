#requires -Version 7.4
# Native assertions: no Pester/network/service/admin dependency.
. (Join-Path $PSScriptRoot 'Common.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "d3parking-deployment-tests-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $testRoot
$passed = 0
function Expect-Failure([scriptblock]$Action, [string]$Name) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw "FAILED: $Name" }
    $script:passed++
    Write-Host "[OK] $Name"
}
function New-TestZip([string]$Path, [string[]]$Names) {
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Names) {
            $entry = $zip.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('test') } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
}
try {
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        if ($errors) { throw ($errors | Out-String) }
    }
    $passed++; Write-Host '[OK] All deployment scripts parse'
    Expect-Failure { Assert-ChildPath $testRoot (Join-Path $testRoot '../outside') } 'Parent traversal rejected'
    Expect-Failure { Get-ServiceCommand 'C:\bad"path' 'C:\safe' Production } 'Service command quoting rejected'
    $archive = Join-Path $testRoot 'bad.zip'
    New-TestZip $archive @('../escaped.txt')
    Expect-Failure { Expand-VerifiedRelease $archive (Join-Path $testRoot 'stage-a') ('0' * 64) } 'Invalid ZIP checksum rejected before extraction'
    Expect-Failure { Expand-VerifiedRelease $archive (Join-Path $testRoot 'stage-b') (Get-FileHash $archive).Hash } 'ZIP traversal rejected before extraction'
    if (Test-Path (Join-Path $testRoot 'stage-b')) { throw 'Preflight wrote extraction directory for an unsafe archive.' }
    $duplicate = Join-Path $testRoot 'duplicates.zip'
    New-TestZip $duplicate @('app/a.dll', 'app/A.dll')
    Expect-Failure { Expand-VerifiedRelease $duplicate (Join-Path $testRoot 'stage-c') (Get-FileHash $duplicate).Hash } 'Case-insensitive duplicate ZIP paths rejected'
    $ads = Join-Path $testRoot 'ads.zip'
    New-TestZip $ads @('app/config.json:payload')
    Expect-Failure { Expand-VerifiedRelease $ads (Join-Path $testRoot 'stage-d') (Get-FileHash $ads).Hash } 'Windows alternate data streams rejected'
    $package = Join-Path $testRoot 'valid'
    $required = @('app/D3Parking.Web.exe','app/D3Parking.Web.dll','app/D3Parking.Web.runtimeconfig.json','app/coreclr.dll','database/migrations.sql')
    foreach ($name in $required) {
        $path = Join-Path $package $name
        $null = New-Item -ItemType Directory -Force -Path (Split-Path $path)
        Set-Content -LiteralPath $path -Value 'fixture'
    }
    $files = @($required | ForEach-Object { @{ path = $_; sha256 = (Get-FileHash (Join-Path $package $_)).Hash } })
    Write-JsonAtomic @{ format=1; runtime='win-x64'; version='1.2.3'; files=$files } (Join-Path $package 'release.json')
    $null = Test-ReleaseDirectory $package
    $passed++; Write-Host '[OK] Complete manifest and file hashes accepted'
    Add-Content -LiteralPath (Join-Path $package 'app/D3Parking.Web.dll') -Value 'tampered'
    Expect-Failure { Test-ReleaseDirectory $package } 'Modified payload rejected'
    $json = Join-Path $testRoot 'state.json'
    Write-JsonAtomic @{ current='1.2.3' } $json
    Write-JsonAtomic @{ current='1.2.4'; previous='1.2.3' } $json
    if ((Read-Json $json).previous -ne '1.2.3') { throw 'Atomic state replacement failed.' }
    $passed++; Write-Host '[OK] Atomic state replacement'
    Write-Host "Deployment checks passed: $passed"
} finally {
    $safe = Assert-ChildPath ([IO.Path]::GetTempPath()) $testRoot
    Assert-NoReparsePoint $safe
    Remove-Item -LiteralPath $safe -Recurse -Force
}
