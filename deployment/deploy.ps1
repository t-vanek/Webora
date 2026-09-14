#requires -Version 7.4
[CmdletBinding(DefaultParameterSetName='Deploy')]
param(
    [Parameter(Mandatory, ParameterSetName='Deploy')][string]$ReleasePath,
    [Parameter(ParameterSetName='Deploy')][string]$ExpectedSha256,
    [Parameter(Mandatory, ParameterSetName='Rollback')][switch]$Rollback,
    [string]$InstallPath = 'C:\D3Parking',
    [switch]$CheckOnly
)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
$root = [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
Assert-NoReparsePoint $root
$policy = Read-Json (Join-Path $root 'config/deployment.json')
if ($policy.Environment -notin @('Production','Staging') -or $policy.ServiceName -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,60}$') { throw 'Invalid deployment environment/service name.' }
$healthUri = [uri]$policy.HealthUrl
if ($healthUri.Scheme -ne 'http' -or $healthUri.Host -ne '127.0.0.1' -or $healthUri.AbsolutePath -ne '/') { throw 'HealthUrl must use http://127.0.0.1:<port>.' }
$publicUri = [uri]$policy.PublicUrl
if ($publicUri.Scheme -ne 'https' -or $publicUri.IsLoopback -or $publicUri.AbsolutePath -ne '/') { throw 'PublicUrl must be the public HTTPS origin.' }
foreach ($dir in @('state','logs','releases','config','secrets','data/keys','backups')) { Assert-NoReparsePoint (Join-Path $root $dir) }
$statePath = Join-Path $root 'state/installation.json'
$journalPath = Join-Path $root 'state/in-progress.json'
$log = Join-Path $root "logs/deploy-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))-$([Guid]::NewGuid().ToString('N')).log"
$lock = $null
$stage = $null
$stopped = $false
$upgradeStarted = $false
$changed = $false
$old = $null
$oldManifest = $null
$completed = $false
function Step([string]$Text) { Write-Host $Text; Add-Content -LiteralPath $log -Value "$([DateTime]::UtcNow.ToString('o')) $Text" }
try {
    $lock = [IO.File]::Open((Join-Path $root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    if (Test-Path -LiteralPath $journalPath) { throw 'An interrupted deployment needs recovery. Read state/in-progress.json and ADMIN-GUIDE.md before proceeding.' }
    $state = if (Test-Path -LiteralPath $statePath) { Read-Json $statePath } else { $null }
    if ($state) {
        $old = Assert-ChildPath (Join-Path $root 'releases') (Join-Path $root "releases/$($state.current)")
        $oldManifest = Test-ReleaseDirectory $old
    }
    $service = Get-CimInstance Win32_Service -Filter "Name='$($policy.ServiceName)'"
    if (-not $service -or $service.StartName -ne "NT SERVICE\$($policy.ServiceName)") { throw 'Expected dedicated Windows service/account is missing. Run initialize.ps1 once.' }
    if ($service.State -notin @('Running','Stopped')) { throw 'Service is changing state. Wait and rerun preflight.' }
    if ($old -and $service.PathName -ne (Get-ServiceCommand (Join-Path $old 'app') $root $policy.Environment)) { throw 'Service path does not match installation state. Investigate before deployment.' }
    if ($old -and $service.State -ne 'Running' -and -not $Rollback) { throw 'Current service is stopped. Use recover.ps1 or deploy.ps1 -Rollback after investigating the outage.' }
    if (-not $old -and $service.State -ne 'Stopped') { throw 'First installation expects a stopped placeholder service.' }
    if ($Rollback) {
        if (-not $state -or -not $state.previous) { throw 'No previous release is recorded.' }
        $target = Assert-ChildPath (Join-Path $root 'releases') (Join-Path $root "releases/$($state.previous)")
        $manifest = Test-ReleaseDirectory $target
    } else {
        $archive = (Resolve-Path -LiteralPath $ReleasePath).Path
        if (-not $ExpectedSha256) { $ExpectedSha256 = (Get-Content -LiteralPath "$archive.sha256" -Raw).Trim() }
        $stage = Join-Path ([IO.Path]::GetTempPath()) "d3parking-stage-$([Guid]::NewGuid().ToString('N'))"
        $manifest = Expand-VerifiedRelease $archive $stage $ExpectedSha256
        $target = Assert-ChildPath (Join-Path $root 'releases') (Join-Path $root "releases/$($manifest.version)")
        if (Test-Path -LiteralPath $target) { throw 'Target release already exists. Never overwrite a versioned release; choose another version or use -Rollback.' }
    }
    if ($manifest.dirty) { throw 'Production/Staging deployment refuses a dirty working-tree artifact. Build from a reviewed Git commit.' }
    Step "D3Parking deployment | Environment: $($policy.Environment) | Current: $(if ($state) { $state.current } else { 'not installed' }) | Target: $($manifest.version)"
    Step '[OK] Release integrity and service identity'
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($root))
    $source = if ($stage) { $stage } else { $target }
    $size = (Get-ChildItem -LiteralPath $source -Recurse -File | Measure-Object Length -Sum).Sum
    if ($drive.AvailableFreeSpace -lt ($size * 2 + 1GB)) { throw 'Insufficient free disk space. Free space without deleting current or previous releases.' }
    Step '[OK] Disk space'
    $shared = Read-Json (Join-Path $root 'config/appsettings.json')
    if ($shared.Deployment.Environment -ne $policy.Environment -or $shared.Kestrel.Endpoints.Health.Url.TrimEnd('/') -ne $policy.HealthUrl.TrimEnd('/') -or $shared.Account.BaseUrl.TrimEnd('/') -ne $policy.PublicUrl.TrimEnd('/')) {
        throw 'Application and deployment environment/URLs must match.'
    }
    foreach ($path in @((Join-Path $root 'config/appsettings.json'), (Join-Path $root 'secrets/secrets.json'), $shared.Kestrel.Endpoints.Public.Certificate.Path, $shared.DataProtection.Certificate.Path)) {
        Assert-ServiceAccess $path $policy.ServiceName ([Security.AccessControl.FileSystemRights]::Read)
    }
    foreach ($dir in @('logs','data/keys')) { Assert-ServiceAccess (Join-Path $root $dir) $policy.ServiceName ([Security.AccessControl.FileSystemRights]::Modify) }
    Step '[OK] Service access to shared configuration, certificates, logs and keys'
    # Exercise administrator access to the actual state and release directories before stopping.
    foreach ($dir in @('state','releases','backups')) {
        $probe = Join-Path $root "$dir/.write-test-$([Guid]::NewGuid().ToString('N'))"
        [IO.File]::WriteAllText($probe, '')
        Remove-Item -LiteralPath $probe
    }
    $preflightPath = "$log.preflight.json"
    $check = Invoke-ReleaseCommand (Join-Path $source 'app') $root $policy.Environment 'preflight' $preflightPath
    if (-not $check.success) { throw "Preflight failed at $($check.phase): $($check.reason)" }
    Step "[OK] Shared configuration, certificates, SQL permissions and SMTP | Pending database migrations: $($check.schema.pending.Count)"
    Add-Content -LiteralPath $log -Value ($check | ConvertTo-Json -Depth 20)
    if ($check.unapprovedMigrations.Count -gt 0) { throw "Review database/migrations.sql and approve these exact IDs in config/deployment.json: $($check.unapprovedMigrations -join ', ')" }
    if ($Rollback -and $check.schema.pending.Count -gt 0) { throw 'Rollback target schema does not match the database. Database restoration requires DBA recovery.' }
    if ($check.release.version -ne $manifest.version -or $check.release.commit -ne $manifest.commit -or $check.schema.target -ne $manifest.schema) { throw 'Executable identity/schema differs from release manifest.' }
    if ($old -and -not $Rollback) { Wait-ReleaseHealthy $policy.HealthUrl $oldManifest $policy.Environment; Step '[OK] Current application readiness' }
    elseif (-not $old) {
        foreach ($port in @($healthUri.Port, $publicUri.Port)) {
            if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port $port is already occupied." }
        }
    }
    if ($CheckOnly) { Step '[OK] Preflight completed. Application, service and database were not modified.'; $completed = $true; return }
    Write-JsonAtomic @{ previous = if ($state) { $state.current } else { $null }; target = $manifest.version; phase = 'prepared'; log = $log } $journalPath
    # Copy after all critical preflight checks; never overwrite current/previous releases.
    if ($stage) {
        $null = New-Item -ItemType Directory -Path $target
        Copy-Item -Path (Join-Path $stage '*') -Destination $target -Recurse
        $null = Test-ReleaseDirectory $target
        Assert-ServiceAccess (Join-Path $target 'app/D3Parking.Web.exe') $policy.ServiceName ([Security.AccessControl.FileSystemRights]::ReadAndExecute)
    }
    $stopped = $true
    if ($old) { Stop-Service -Name $policy.ServiceName; (Get-Service $policy.ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60)) }
    Step '[OK] Application stopped'
    Write-JsonAtomic @{ previous = if ($state) { $state.current } else { $null }; target = $manifest.version; phase = 'database upgrade'; log = $log } $journalPath
    $upgradeStarted = $true
    $expectedSchema = if ($check.schema.applied.Count) { $check.schema.applied[-1] } else { 'none' }
    $upgrade = Invoke-ReleaseCommand (Join-Path $target 'app') $root $policy.Environment 'upgrade' "$log.upgrade.json" $expectedSchema
    $changed = $upgrade.databaseMayHaveChanged
    $upgradeStarted = $false
    if (-not $upgrade.success) { throw "Upgrade failed at $($upgrade.phase): $($upgrade.reason)" }
    Step "[OK] Verified SQL backup: $($upgrade.backup)"
    Step '[OK] Database schema'
    Write-JsonAtomic @{ previous = if ($state) { $state.current } else { $null }; target = $manifest.version; phase = 'starting target'; backup = $upgrade.backup; schemaChanged = $changed; log = $log } $journalPath
    Set-ReleaseService $policy.ServiceName (Get-ServiceCommand (Join-Path $target 'app') $root $policy.Environment)
    Start-Service -Name $policy.ServiceName
    Wait-ReleaseHealthy $policy.HealthUrl $manifest $policy.Environment
    Wait-ReleaseHealthy $policy.PublicUrl $manifest $policy.Environment
    Step '[OK] Local and public HTTPS readiness, database and release identity'
    & sc.exe config $policy.ServiceName 'start=' 'delayed-auto' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set automatic service startup.' }
    Write-JsonAtomic @{ current = $manifest.version; previous = if ($state) { $state.current } else { $null }; schema = $manifest.schema; backup = $upgrade.backup; deployedAt = [DateTime]::UtcNow.ToString('o') } $statePath
    $completed = $true
    Remove-Item -LiteralPath $journalPath
    Step 'Deployment completed successfully.'
} catch {
    Step "[FAILED] $($_.Exception.Message)"
    if ($completed) {
        Step 'The target release passed health checks and installation state was committed. It remains running. Resolve the remaining journal/log write error before the next deployment.'
    } elseif ($stopped -and $old -and -not $changed -and -not $upgradeStarted) {
        try {
            Stop-Service -Name $policy.ServiceName -ErrorAction SilentlyContinue
            (Get-Service $policy.ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
            Set-ReleaseService $policy.ServiceName (Get-ServiceCommand (Join-Path $old 'app') $root $policy.Environment)
            Start-Service -Name $policy.ServiceName
            Wait-ReleaseHealthy $policy.HealthUrl $oldManifest $policy.Environment
            Remove-Item -LiteralPath $journalPath -ErrorAction SilentlyContinue
            Step 'Previous application restored; database schema was not changed.'
        } catch { Step 'Automatic application recovery failed. Keep the installation under maintenance; follow ADMIN-GUIDE.md.' }
    } elseif ($stopped) {
        Stop-Service -Name $policy.ServiceName -ErrorAction SilentlyContinue
        Step 'Application is stopped. Database may have changed. Do not start an older release; follow the database recovery procedure.'
    } else {
        if (Test-Path -LiteralPath $journalPath) { Step 'Preparation was interrupted; review state/in-progress.json.' }
        Step 'Application and database were not modified.'
    }
    Write-Host "Details: $log"
    exit 1
} finally {
    if ($lock) { $lock.Dispose() }
    # Staging is a unique direct child of the OS temp directory; never remove a computed production path.
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        $safeStage = Assert-ChildPath ([IO.Path]::GetTempPath()) $stage
        Assert-NoReparsePoint $safeStage
        Remove-Item -LiteralPath $safeStage -Recurse -Force
    }
}
