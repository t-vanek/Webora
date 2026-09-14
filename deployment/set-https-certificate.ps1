#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$CertificatePath, [string]$InstallPath = 'C:\D3Parking')
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
$root = [IO.Path]::GetFullPath($InstallPath)
Assert-NoReparsePoint $root
$policy = Read-Json (Join-Path $root 'config/deployment.json')
$lock = [IO.File]::Open((Join-Path $root 'state/deploy.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
if (Test-Path -LiteralPath (Join-Path $root 'state/in-progress.json')) { throw 'Complete interrupted deployment recovery before changing its certificate.' }
$secure = Read-Host 'HTTPS PFX password' -AsSecureString
$password = [Net.NetworkCredential]::new('', $secure).Password
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
    (Resolve-Path -LiteralPath $CertificatePath).Path, $password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
try {
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -lt [DateTime]::Now -or -not $certificate.MatchesHostname(([uri]$policy.PublicUrl).Host, $true, $false)) {
        throw 'PFX needs a current certificate, its private key, and a matching SAN hostname.'
    }
} finally { $certificate.Dispose() }
# Retain previous PFX files for recovery; configuration points at the newly installed one.
$name = "site-$([DateTime]::UtcNow.ToString('yyyyMMddTHHmmss'))-$([Guid]::NewGuid().ToString('N')).pfx"
$target = Join-Path $root "secrets/$name"
Copy-Item -LiteralPath $CertificatePath -Destination $target
$sid = ([Security.Principal.NTAccount]::new("NT SERVICE\$($policy.ServiceName)")).Translate([Security.Principal.SecurityIdentifier])
$acl = Get-Acl -LiteralPath $target
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'Read', 'Allow'))
Set-Acl -LiteralPath $target -AclObject $acl
$appPath = Join-Path $root 'config/appsettings.json'
$secretPath = Join-Path $root 'secrets/secrets.json'
$app = Read-Json $appPath
$secrets = Read-Json $secretPath
$backupDir = Join-Path $root "backups/certificate-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $backupDir
Copy-Item -LiteralPath $appPath, $secretPath -Destination $backupDir
$app.Kestrel.Endpoints.Public.Certificate.Path = $target
$secrets.Kestrel.Endpoints.Public.Certificate.Password = $password
Write-JsonAtomic $secrets $secretPath
Write-JsonAtomic $app $appPath
Write-Host "[OK] HTTPS certificate configured. Previous configuration: $backupDir. Run preflight before restarting."
} finally { $lock.Dispose(); $password = $null }
