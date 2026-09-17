#requires -Version 5.1
# The entry point deliberately supports Windows PowerShell 5.1 so a right-click launch
# can explain the PowerShell 7 requirement and keep the error visible.
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputPath,
    [string]$WorkPath,
    [switch]$AllowDirty,
    [switch]$CheckOnly,
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$buildExitCode = 0
$locationPushed = $false
$transcriptStarted = $false
$buildLog = $null
$work = $null
function New-BuildWorkDirectory([string]$BasePath) {
    # Never reuse or clear a caller-owned directory. Keep SDK intermediates out of
    # the source tree: WebAssembly output paths otherwise exceed Windows limits.
    $null = New-Item -ItemType Directory -Force -Path $BasePath
    $candidate = Join-Path $BasePath ('d3b-' + [Guid]::NewGuid().ToString('N').Substring(0, 16))
    (New-Item -ItemType Directory -Path $candidate).FullName
}
$pauseAtEnd = -not $NoPause -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected -and
    -not ([Environment]::GetCommandLineArgs() -contains '-NonInteractive') -and -not $env:CI
try {
    $logDirectory = Join-Path $PSScriptRoot 'artifacts'
    try { $null = New-Item -ItemType Directory -Force -Path $logDirectory }
    catch {
        $logDirectory = Join-Path ([IO.Path]::GetTempPath()) 'D3Parking-build-logs'
        $null = New-Item -ItemType Directory -Force -Path $logDirectory
    }
    $buildLog = Join-Path $logDirectory "build-release-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss'))-$([Guid]::NewGuid().ToString('N')).log"
    $null = Start-Transcript -LiteralPath $buildLog
    $transcriptStarted = $true
    Write-Host 'D3Parking - sestavení release ze zdrojového repozitáře' -ForegroundColor Cyan
    Write-Host "Protokol tohoto spuštění: $buildLog"
    Write-Host "Spuštěný PowerShell: $($PSVersionTable.PSVersion)"
    if ($PSVersionTable.PSVersion -lt [Version]'7.4') {
        throw 'Je potřeba PowerShell 7.4 nebo novější. Volba Spustit pomocí PowerShellu může otevřít starý Windows PowerShell 5.1. Otevřete PowerShell 7 a spusťte .\build-release.ps1. Pokud jej nemáte, požádejte IT o instalaci.'
    }
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'Sestavení vyžaduje Windows x64.' }
    if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot 'artifacts/releases' }
    # Dot-sourcing the deployment library has script parameters of its own. Preserve builder flags.
    $buildCheckOnly = $CheckOnly
    . (Join-Path $PSScriptRoot 'deployment/D3Parking.ps1') -Version $Version
    $CheckOnly = $buildCheckOnly
    Push-Location $PSScriptRoot
    $locationPushed = $true
    Write-Host '[1/5] Kontrola Gitu, SDK, verze a výstupní složky...'
    if (-not (Get-Command git -CommandType Application -ErrorAction SilentlyContinue)) { throw 'Git není dostupný. Spouštějte builder v naklonovaném repozitáři na počítači s nainstalovaným Gitem.' }
    if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) { throw 'Příkaz dotnet není dostupný. Nainstalujte .NET SDK uvedené v global.json; samotný runtime nestačí.' }
    $requiredSdk = (Read-Json (Join-Path $PSScriptRoot 'global.json')).sdk.version
    $selectedSdk = (& dotnet --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $selectedSdk -ne $requiredSdk) { throw "Není dostupné požadované .NET SDK $requiredSdk z global.json. Nainstalujte tuto verzi a spusťte znovu; samotný runtime nestačí." }
    Write-Host "[OK] .NET SDK $selectedSdk"
    if (-not $Version) {
        if (-not $pauseAtEnd) { throw 'Chybí verze vydání. Zadejte například: .\build-release.ps1 -Version 0.1.3 -NoPause. Použijte dosud nevydanou verzi.' }
        $Version = (Read-Host 'Zadejte novou verzi vydání, např. 0.1.3 (Enter zruší sestavení)').Trim()
    }
    if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Verze chybí nebo nemá správný tvar. Příklad: 0.1.3 nebo 0.1.3-test.1. Žádný balíček nebyl sestaven.' }
    $dirty = -not [string]::IsNullOrWhiteSpace((& git status --porcelain | Out-String))
    if ($LASTEXITCODE -ne 0) { throw 'Git repozitář není dostupný. Nestačí samostatný skript nebo ZIP se zdroji; použijte naklonovaný repozitář.' }
    if ($dirty -and -not $AllowDirty) { throw 'Repozitář obsahuje necommitované změny. Nejprve je zkontrolujte a commitněte. -AllowDirty je jen pro místní ověření; takový ZIP nelze nasadit do Production/Staging.' }
    $commit = (& git rev-parse HEAD).Trim()
    if ($dirty) { $commit += '.dirty' }
    $epoch = [long](& git show -s --format=%ct HEAD)
    $stamp = [DateTimeOffset]::FromUnixTimeSeconds($epoch)
    $output = [IO.Path]::GetFullPath($OutputPath)
    if (-not $WorkPath) { $WorkPath = [IO.Path]::GetTempPath() }
    $workBase = [IO.Path]::GetFullPath($WorkPath, $PSScriptRoot)
    if ((Test-Path -LiteralPath $workBase) -and -not (Test-Path -LiteralPath $workBase -PathType Container)) { throw "Pracovní cesta není adresář: $workBase" }
    $null = New-Item -ItemType Directory -Force -Path $output
    $archive = Join-Path $output "D3Parking-$Version-win-x64.zip"
    $wizard = Join-Path $output "D3Parking-$Version.ps1"
    if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath "$archive.sha256") -or (Test-Path -LiteralPath $wizard) -or (Test-Path -LiteralPath "$wizard.sha256")) { throw "Vydání $Version už ve výstupní složce existuje. Zvolte novou verzi; původní ZIP se nepřepisuje." }
    Write-Host "Verze: $Version | Commit: $commit | Výstup: $output"
    Write-Host "Základna pracovních souborů: $workBase"
    if ($CheckOnly) { Write-Host '[OK] Předběžná kontrola dokončena. Build neproběhl; síť pro restore a samotné sestavení se ověří až při build kroku.'; return }
    $work = New-BuildWorkDirectory $workBase
    $package = Join-Path $work 'package'
    $app = Join-Path $package 'app'
    $database = Join-Path $package 'database'
    $null = New-Item -ItemType Directory -Force -Path $app, $database
    Write-Host '[2/5] Obnovení závislostí a sestavení. Může trvat několik minut; toto okno nezavírejte.'
    Write-Host "Podrobný průběh sestavení: $work/publish.log"
    & dotnet publish src/D3Parking.Web/D3Parking.Web.csproj -c Release --self-contained true -r win-x64 --artifacts-path (Join-Path $work 'build') -o $app `
        "-p:Version=$Version" "-p:SourceRevisionId=$commit" '-p:ContinuousIntegrationBuild=true' '-p:RestoreLockedMode=true' '-p:DebugType=None' '-p:DebugSymbols=false' *> (Join-Path $work 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw "Sestavení dotnet publish selhalo. Přesná chyba je v $work/publish.log. Pokud chyba souvisí s délkou cesty, zvolte kratší zapisovatelnou základnu pomocí -WorkPath (např. C:\BuildWork)." }
    # Publish only application outputs. Development credentials must never enter a release.
    $forbidden = Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Name -match '(?i)(^appsettings\.(Development|.*local)\.json(\.(br|gz))?$|^secrets(\..*)?\.json$|\.(pfx|p12|pem|key|log|bak|mdf|ldf|cs|csproj)$)' }
    if ($forbidden) { throw "Výstup obsahuje nepovolené vývojové nebo soukromé soubory: $($forbidden.Name -join ', ')" }
    Write-Host '[3/5] Kontrola publikované aplikace a vytvoření migračního SQL...'
    $metadataFile = Join-Path $work 'metadata.json'
    & (Join-Path $app 'D3Parking.Web.exe') --contentRoot $app --deployment-command manifest --deployment-report $metadataFile --deployment-sql (Join-Path $database 'migrations.sql')
    if ($LASTEXITCODE -ne 0) { throw 'Publikovaná aplikace nedokázala vytvořit metadata vydání. Prohlédněte tento protokol.' }
    $metadata = Read-Json $metadataFile
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'deployment/D3Parking.ps1') -Destination (Join-Path $package 'D3Parking.ps1')
    $null = New-Item -ItemType Directory -Path (Join-Path $package 'docs')
    foreach ($name in @('ADMIN-GUIDE.md', 'DEPLOYMENT.md', 'CONFIGURATION.md', 'AUDIT.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs/$name") -Destination (Join-Path $package "docs/$name")
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $package
    Write-Host '[4/5] Přiložení průvodce, dokumentace a kontrolních součtů souborů...'
    $files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    $manifest = [ordered]@{ format = 1; version = $metadata.release.version; commit = $metadata.release.commit; dirty = $dirty;
        buildTimestamp = $stamp.ToString('o'); timestampSource = 'git commit (SOURCE_DATE_EPOCH)'; runtime = 'win-x64'; framework = $metadata.release.runtime;
        schema = $metadata.migrations[-1]; migrations = @($metadata.migrations); files = $files }
    Write-JsonAtomic $manifest (Join-Path $package 'release.json')
    $null = Test-ReleaseDirectory $package
    # Stable entry order/timestamps; build metadata uses the source commit date, not wall-clock time.
    Write-Host '[5/5] Zabalení ZIPu a ověření výsledku...'
    $temporaryArchive = Join-Path $work 'release.zip'
    $zip = [IO.Compression.ZipFile]::Open($temporaryArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $package -Recurse -File | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($package, $file.FullName).Replace('\', '/')
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $inputStream = [IO.File]::OpenRead($file.FullName)
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
    } finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporaryArchive -Destination $archive
    $hash = (Get-FileHash -LiteralPath $archive).Hash
    Set-Content -LiteralPath "$archive.sha256" -Value $hash -Encoding ascii
    Copy-Item -LiteralPath (Join-Path $package 'D3Parking.ps1') -Destination $wizard
    Set-Content -LiteralPath "$wizard.sha256" -Value (Get-FileHash -LiteralPath $wizard).Hash -Encoding ascii
    Write-Host "[OK] $archive"
    Write-Host "[OK] Samostatný instalační průvodce: $wizard"
    Write-Host "SHA256: $hash"
    if ($dirty) { Write-Host '[POZOR] Balíček pro místní ověření: dirty=true. Produkční nasazení jej odmítne.' }
} catch {
    $buildExitCode = 1
    Write-Host ("[CHYBA] " + $_.Exception.Message) -ForegroundColor Red
    Write-Host "Místo chyby: řádek $($_.InvocationInfo.ScriptLineNumber)"
} finally {
    if ($locationPushed) { Pop-Location }
    if ($buildLog) { Write-Host "Protokol k dohledání chyby: $buildLog" }
    if ($transcriptStarted) { $null = Stop-Transcript }
    if ($pauseAtEnd) {
        try { $null = Read-Host 'Stiskněte Enter pro zavření / návrat do konzole' } catch { }
    }
}
exit $buildExitCode
