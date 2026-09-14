#requires -Version 7.4
# Real child-process startup checks; no publishing, Windows service or administrator required.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'D3Parking.ps1')
$builder = Join-Path (Split-Path $PSScriptRoot) 'build-release.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) "d3parking-builder-tests-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $testRoot
$passed = 0
function Check-Startup([string]$Executable, [string[]]$Arguments, [int]$ExitCode, [string]$ExpectedText) {
    $output = & $Executable -NoProfile -NonInteractive -File $builder @Arguments -NoPause 2>&1 | Out-String
    if ($LASTEXITCODE -ne $ExitCode -or -not $output.Contains($ExpectedText)) { throw "Unexpected builder result: $output" }
    $logLine = @($output -split '\r?\n' | Where-Object { $_.StartsWith('Protokol k dohledání chyby: ') })
    if ($logLine.Count -ne 1) { throw 'Builder did not report a persistent log.' }
    $log = $logLine[0].Substring('Protokol k dohledání chyby: '.Length).Trim()
    if (-not (Test-Path -LiteralPath $log) -or -not (Get-Content -LiteralPath $log -Raw).Contains($ExpectedText)) { throw 'Builder log is missing the failure/check result.' }
    $script:passed++
    Write-Host "[OK] $ExpectedText"
}
try {
    $pwsh = Join-Path $PSHOME 'pwsh.exe'
    $legacy = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    Check-Startup $legacy @() 1 'Je potřeba PowerShell 7.4'
    Check-Startup $pwsh @() 1 'Chybí verze vydání'
    Check-Startup $pwsh @('-Version','bad-version') 1 'Verze chybí nebo nemá správný tvar'
    $occupied = Join-Path $testRoot 'occupied'
    $null = New-Item -ItemType Directory -Path $occupied
    Set-Content -LiteralPath (Join-Path $occupied 'D3Parking-1.2.3-win-x64.zip') -Value 'existing release must stay untouched'
    Check-Startup $pwsh @('-Version','1.2.3','-OutputPath',$occupied,'-CheckOnly','-AllowDirty') 1 'Vydání 1.2.3 už'
    Check-Startup $pwsh @('-Version','1.2.4','-OutputPath',$testRoot,'-CheckOnly','-AllowDirty') 0 'Předběžná kontrola dokončena'
    if (Test-Path -LiteralPath (Join-Path $testRoot 'D3Parking-1.2.4-win-x64.zip')) { throw 'CheckOnly unexpectedly published a release.' }
    Write-Host "Builder startup checks passed: $passed"
} finally {
    $safe = Assert-ChildPath ([IO.Path]::GetTempPath()) $testRoot
    Assert-NoReparsePoint $safe
    Remove-Item -LiteralPath $safe -Recurse -Force
}
