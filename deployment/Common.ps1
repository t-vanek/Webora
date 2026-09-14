#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ChildPath([string]$Parent, [string]$Path) {
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes its parent: $Path" }
    $full
}

function Assert-NoReparsePoint([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Links/junctions are not allowed: $($item.FullName)" }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
}

function Write-JsonAtomic([object]$Value, [string]$Path) {
    $temp = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $Value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $temp -Encoding utf8NoBOM
    if (Test-Path -LiteralPath $Path) { Set-Acl -LiteralPath $temp -AclObject (Get-Acl -LiteralPath $Path) }
    [IO.File]::Move($temp, $Path, $true)
}

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }

function Assert-Administrator {
    if (-not $IsWindows) { throw 'Deployment requires Windows x64.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Open PowerShell 7 as Administrator, then run the command again.'
    }
}

function Assert-ServiceAccess([string]$Path, [string]$ServiceName, [Security.AccessControl.FileSystemRights]$Rights) {
    Assert-NoReparsePoint $Path
    $sid = ([Security.Principal.NTAccount]::new("NT SERVICE\$ServiceName")).Translate([Security.Principal.SecurityIdentifier]).Value
    [long]$granted = 0
    foreach ($rule in (Get-Acl -LiteralPath $Path).Access) {
        if ($rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -eq $sid) {
            if ($rule.AccessControlType -eq 'Deny') { throw "Service access denied: $Path" }
            $granted = $granted -bor [long]$rule.FileSystemRights
        }
    }
    if (($granted -band [long]$Rights) -ne [long]$Rights) { throw "Service $ServiceName lacks $Rights on $Path. Follow the ACL instructions in ADMIN-GUIDE.md." }
}

function Expand-VerifiedRelease([string]$Archive, [string]$Destination, [string]$ExpectedSha256) {
    if ($ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Missing or invalid SHA-256 checksum.' }
    if ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw 'Release checksum mismatch. Application was not modified.' }
    if (Test-Path -LiteralPath $Destination) { throw 'Release extraction directory already exists.' }
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$size = 0
        if ($zip.Entries.Count -gt 20000) { throw 'Release has too many entries.' }
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            if ($name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/') -or $name -match '(^|/)\.\.?(/|$)' -or $name -match '[. ](/|$)') {
                throw 'Release contains an unsafe path.'
            }
            if (-not $seen.Add($name)) { throw 'Release contains duplicate paths.' }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Release contains a symbolic link.' }
            $null = Assert-ChildPath $Destination (Join-Path $Destination $name)
            $size += $entry.Length
            if ($size -gt 4GB) { throw 'Release exceeds the 4 GiB unpacked limit.' }
        }
        $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Destination)))
        if ($drive.AvailableFreeSpace -lt ($size + 512MB)) { throw 'Insufficient disk space for release extraction.' }
        $null = New-Item -ItemType Directory -Path $Destination
        foreach ($entry in $zip.Entries) {
            $target = Assert-ChildPath $Destination (Join-Path $Destination $entry.FullName)
            if ($entry.FullName.EndsWith('/')) { $null = New-Item -ItemType Directory -Force -Path $target; continue }
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $target)
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        }
    } finally { $zip.Dispose() }
    Test-ReleaseDirectory $Destination
}

function Test-ReleaseDirectory([string]$Path) {
    Assert-NoReparsePoint $Path
    $manifest = Read-Json (Join-Path $Path 'release.json')
    if ($manifest.format -ne 1 -or $manifest.runtime -ne 'win-x64' -or $manifest.version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw 'Unsupported release metadata.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $manifest.files) {
        if (-not $seen.Add($file.path)) { throw 'Duplicate manifest entry.' }
        $target = Assert-ChildPath $Path (Join-Path $Path $file.path)
        Assert-NoReparsePoint $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256) { throw "Release file checksum mismatch: $($file.path)" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Path -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($Path, $file.FullName).Replace('\', '/')
        if ($relative -ne 'release.json' -and -not $seen.Contains($relative)) { throw "Unexpected release file: $relative" }
    }
    foreach ($required in @('app/D3Parking.Web.exe', 'app/D3Parking.Web.dll', 'app/D3Parking.Web.runtimeconfig.json', 'app/coreclr.dll', 'database/migrations.sql')) {
        if (-not $seen.Contains($required)) { throw "Release is incomplete: $required" }
    }
    $manifest
}

function Invoke-ReleaseCommand([string]$AppPath, [string]$Root, [string]$Environment, [string]$Command, [string]$ReportPath, [string]$ExpectedSchema = '') {
    if (Test-Path -LiteralPath $ReportPath) { throw 'Maintenance report already exists; use a new report path.' }
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $AppPath 'D3Parking.Web.exe'))
    $start.WorkingDirectory = $AppPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($value in @('--contentRoot', $AppPath, '--environment', $Environment, '--Deployment:InstallPath', $Root,
        '--deployment-command', $Command, '--deployment-report', $ReportPath, '--deployment-expected-schema', $ExpectedSchema)) { $start.ArgumentList.Add($value) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        # Drain both streams concurrently; a full stderr pipe must not deadlock maintenance.
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(2500000)) { $process.Kill($true); throw 'Maintenance timed out; inspect database before recovery.' }
        $null = $output.GetAwaiter().GetResult()
        $null = $errors.GetAwaiter().GetResult()
        if (-not (Test-Path -LiteralPath $ReportPath)) { throw "Maintenance process failed before its report (exit $($process.ExitCode)). Check shared JSON files and certificates." }
        $result = Read-Json $ReportPath
        if ($process.ExitCode -ne 0 -and $result.success) { throw 'Maintenance exited unsuccessfully despite its report. Inspect the database before recovery.' }
        return $result
    } finally { $process.Dispose() }
}

function Get-ServiceCommand([string]$AppPath, [string]$Root, [string]$Environment) {
    foreach ($value in @($AppPath, $Root, $Environment)) { if ($value.Contains('"')) { throw 'Quotes are not allowed in installation paths.' } }
    '"{0}" --contentRoot "{1}" --Deployment:InstallPath "{2}" --environment "{3}"' -f (Join-Path $AppPath 'D3Parking.Web.exe'), $AppPath, $Root, $Environment
}

function Set-ReleaseService([string]$Name, [string]$BinaryPath) {
    & sc.exe config $Name 'binPath=' $BinaryPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not change Windows service release path.' }
}

function Wait-ReleaseHealthy([string]$Url, [object]$Manifest, [string]$Environment) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        try {
            $response = Invoke-RestMethod -Uri "$($Url.TrimEnd('/'))/health/ready" -TimeoutSec 6 -MaximumRedirection 0 -NoProxy
            if ($response.status -eq 'ready' -and $response.release.version -eq $Manifest.version -and $response.release.commit -eq $Manifest.commit -and $response.release.environment -eq $Environment) { return }
        } catch { }
        Start-Sleep -Milliseconds 1000
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Health check failed or a different release answered. Inspect application logs.'
}
