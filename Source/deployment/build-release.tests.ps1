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
    $script:lastStartupOutput = $output
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
    if (-not $lastStartupOutput.Contains("Základna pracovních souborů: $([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))")) { throw 'Default intermediates are not rooted in the system temporary directory.' }
    $workBase = Join-Path $testRoot 'pracovní soubory'
    Check-Startup $pwsh @('-Version','1.2.4','-OutputPath',$testRoot,'-WorkPath',$workBase,'-CheckOnly','-AllowDirty') 0 "Základna pracovních souborů: $workBase"
    if (Test-Path -LiteralPath $workBase) { throw 'CheckOnly unexpectedly created build intermediates.' }
    $occupiedWork = Join-Path $testRoot 'work-is-a-file'
    Set-Content -LiteralPath $occupiedWork -Value 'preserve this file' -NoNewline
    Check-Startup $pwsh @('-Version','1.2.4','-OutputPath',$testRoot,'-WorkPath',$occupiedWork,'-CheckOnly','-AllowDirty') 1 'Pracovní cesta není adresář'
    if ((Get-Content -LiteralPath $occupiedWork -Raw) -ne 'preserve this file') { throw 'WorkPath replaced an existing file.' }

    # Load the actual allocator without running the builder's publish entry point.
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($builder, [ref]$null, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "Builder parse error: $parseErrors" }
    $allocator = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'New-BuildWorkDirectory' }, $false)
    if (-not $allocator) { throw 'Builder work-directory allocator is missing.' }
    . ([scriptblock]::Create($allocator.Extent.Text))
    $firstWork = New-BuildWorkDirectory $workBase
    $sentinel = Join-Path $firstWork 'publish.log'
    Set-Content -LiteralPath $sentinel -Value 'previous failed build' -NoNewline
    $secondWork = New-BuildWorkDirectory $workBase
    if ($firstWork -eq $secondWork -or -not (Test-Path -LiteralPath $secondWork -PathType Container)) { throw 'Repeated build reused an existing working directory.' }
    if ((Get-Content -LiteralPath $sentinel -Raw) -ne 'previous failed build') { throw 'Repeated build changed previous diagnostic files.' }
    if ((Split-Path $firstWork -Parent) -ne $workBase -or (Split-Path $secondWork -Leaf).Length -gt 20) { throw 'Build intermediates do not use a short child of WorkPath.' }
    $passed++
    Write-Host '[OK] Krátké unikátní pracovní adresáře zachovávají předchozí mezivýsledky a logy.'
    Write-Host "Builder startup checks passed: $passed"
} finally {
    $safe = Assert-ChildPath ([IO.Path]::GetTempPath()) $testRoot
    Assert-NoReparsePoint $safe
    Remove-Item -LiteralPath $safe -Recurse -Force
}
