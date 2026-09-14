#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'artifacts/releases'),
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'Build this win-x64 release on Windows x64.' }
. (Join-Path $PSScriptRoot 'deployment/Common.ps1')
Push-Location $PSScriptRoot
$work = Join-Path $PSScriptRoot "artifacts/release-build-$([Guid]::NewGuid().ToString('N'))"
try {
    $dirty = -not [string]::IsNullOrWhiteSpace((& git status --porcelain | Out-String))
    if ($LASTEXITCODE -ne 0) { throw 'Git repository unavailable.' }
    if ($dirty -and -not $AllowDirty) { throw 'Commit the reviewed changes first. -AllowDirty is for local validation only.' }
    $commit = (& git rev-parse HEAD).Trim()
    if ($dirty) { $commit += '.dirty' }
    $epoch = [long](& git show -s --format=%ct HEAD)
    $stamp = [DateTimeOffset]::FromUnixTimeSeconds($epoch)
    $output = [IO.Path]::GetFullPath($OutputPath)
    $null = New-Item -ItemType Directory -Force -Path $output, $work
    $archive = Join-Path $output "D3Parking-$Version-win-x64.zip"
    if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath "$archive.sha256")) { throw 'This release version already exists. Use a new version.' }
    $package = Join-Path $work 'package'
    $app = Join-Path $package 'app'
    $database = Join-Path $package 'database'
    $null = New-Item -ItemType Directory -Force -Path $app, $database
    Write-Host 'D3Parking release: restoring locked dependencies and publishing...'
    & dotnet publish src/D3Parking.Web/D3Parking.Web.csproj -c Release --self-contained true -r win-x64 --artifacts-path (Join-Path $work 'build') -o $app `
        "-p:Version=$Version" "-p:SourceRevisionId=$commit" '-p:ContinuousIntegrationBuild=true' '-p:RestoreLockedMode=true' '-p:DebugType=None' '-p:DebugSymbols=false' *> (Join-Path $work 'publish.log')
    if ($LASTEXITCODE -ne 0) { throw "Publish failed. Details: $work/publish.log" }
    # Publish only application outputs. Development credentials must never enter a release.
    $forbidden = Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Name -match '(?i)(^appsettings\.(Development|.*local)\.json(\.(br|gz))?$|^secrets(\..*)?\.json$|\.(pfx|p12|pem|key|log|bak|mdf|ldf|cs|csproj)$)' }
    if ($forbidden) { throw "Publish contains development/private/source files: $($forbidden.Name -join ', ')" }
    $metadataFile = Join-Path $work 'metadata.json'
    & (Join-Path $app 'D3Parking.Web.exe') --contentRoot $app --deployment-command manifest --deployment-report $metadataFile --deployment-sql (Join-Path $database 'migrations.sql')
    if ($LASTEXITCODE -ne 0) { throw 'Published executable cannot produce release metadata.' }
    $metadata = Read-Json $metadataFile
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'deployment') -Destination $package -Recurse
    $null = New-Item -ItemType Directory -Path (Join-Path $package 'docs')
    foreach ($name in @('ADMIN-GUIDE.md', 'DEPLOYMENT.md', 'CONFIGURATION.md', 'AUDIT.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs/$name") -Destination (Join-Path $package "docs/$name")
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $package
    $files = @(Get-ChildItem -LiteralPath $package -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($package, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    $manifest = [ordered]@{ format = 1; version = $metadata.release.version; commit = $metadata.release.commit; dirty = $dirty;
        buildTimestamp = $stamp.ToString('o'); timestampSource = 'git commit (SOURCE_DATE_EPOCH)'; runtime = 'win-x64'; framework = $metadata.release.runtime;
        schema = $metadata.migrations[-1]; migrations = @($metadata.migrations); files = $files }
    Write-JsonAtomic $manifest (Join-Path $package 'release.json')
    $null = Test-ReleaseDirectory $package
    # Stable entry order/timestamps; build metadata uses the source commit date, not wall-clock time.
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
    Write-Host "[OK] $archive"
    Write-Host "SHA256: $hash"
    if ($dirty) { Write-Host 'Local validation artifact: dirty=true. Production deployment refuses it.' }
} finally { Pop-Location }
