#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$InstallPath = 'C:\D3Parking', [switch]$CheckOnly)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
$root = [IO.Path]::GetFullPath($InstallPath)
Assert-NoReparsePoint $root
$policy = Read-Json (Join-Path $root 'config/deployment.json')
if ($policy.Environment -notin @('Production','Staging') -or $policy.ServiceName -notmatch '^[A-Za-z][A-Za-z0-9_-]{0,60}$') { throw 'Invalid deployment environment/service name.' }
$service = Get-CimInstance Win32_Service -Filter "Name='$($policy.ServiceName)'"
if (-not $service -or $service.StartName -ne "NT SERVICE\$($policy.ServiceName)") { throw 'Recovery requires the initialized service identity.' }
$target = Assert-ChildPath (Join-Path $root 'releases') (Join-Path $root "releases/$Version")
$manifest = Test-ReleaseDirectory $target
if ($manifest.dirty) { throw 'Recovery refuses a dirty artifact.' }
$lock = [IO.File]::Open((Join-Path $root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$report = Join-Path $root "logs/recover-$([Guid]::NewGuid().ToString('N')).json"
try {
    if ((Get-Service $policy.ServiceName).Status -ne 'Stopped') { throw 'Recovery requires a stopped service. Use deploy.ps1 -Rollback for a running installation.' }
    $check = Invoke-ReleaseCommand (Join-Path $target 'app') $root $policy.Environment preflight $report
    if (-not $check.success) { throw "Recovery preflight failed: $($check.reason). Details: $report" }
    if ($check.schema.pending.Count -ne 0) { throw 'Database schema does not exactly match the selected release. Have the DBA restore the correct recovery point first.' }
    if ($check.release.version -ne $manifest.version -or $check.release.commit -ne $manifest.commit -or $check.schema.target -ne $manifest.schema) { throw 'Release identity mismatch.' }
    Write-Host '[OK] Selected release matches the actual database schema; no migration will run.'
    if ($CheckOnly) { return }
    Write-JsonAtomic @{ target = $Version; phase = 'recovery start'; log = $report } (Join-Path $root 'state/in-progress.json')
    Set-ReleaseService $policy.ServiceName (Get-ServiceCommand (Join-Path $target 'app') $root $policy.Environment)
    Start-Service $policy.ServiceName
    try {
        Wait-ReleaseHealthy $policy.HealthUrl $manifest $policy.Environment
        Wait-ReleaseHealthy $policy.PublicUrl $manifest $policy.Environment
    } catch { Stop-Service $policy.ServiceName; throw }
    & sc.exe config $policy.ServiceName 'start=' 'delayed-auto' | Out-Null
    if ($LASTEXITCODE -ne 0) { Stop-Service $policy.ServiceName; throw 'Could not restore automatic startup.' }
    try {
        Write-JsonAtomic @{ current = $Version; previous = $null; schema = $manifest.schema; backup = 'See recovery log / DBA restore record'; deployedAt = [DateTime]::UtcNow.ToString('o') } (Join-Path $root 'state/installation.json')
    } catch { Stop-Service $policy.ServiceName; throw }
    Remove-Item -LiteralPath (Join-Path $root 'state/in-progress.json') -ErrorAction SilentlyContinue
    Write-Host "[OK] Recovery completed. Details: $report"
} finally { $lock.Dispose() }
