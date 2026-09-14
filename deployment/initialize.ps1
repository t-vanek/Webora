#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$InstallPath = 'C:\D3Parking',
    [Parameter(Mandatory)][uri]$PublicUrl,
    [ValidateSet('Production', 'Staging')][string]$Environment = 'Production',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_-]{0,60}$')][string]$ServiceName = 'D3Parking',
    [ValidateRange(1024, 65535)][int]$HealthPort = 5081
)
. (Join-Path $PSScriptRoot 'Common.ps1')
Assert-Administrator
if (-not [IO.Path]::IsPathFullyQualified($InstallPath)) { throw 'InstallPath must be absolute.' }
$root = [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
if (Test-Path -LiteralPath $root) { throw 'InstallPath already exists. Initialization never overwrites an installation.' }
Assert-NoReparsePoint (Split-Path $root)
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) { throw 'Service name is already in use.' }
if ($PublicUrl.Scheme -ne 'https' -or $PublicUrl.IsLoopback -or $PublicUrl.AbsolutePath -ne '/' -or $PublicUrl.UserInfo -or $PublicUrl.Query -or $PublicUrl.Port -eq $HealthPort) {
    throw 'PublicUrl must be the public HTTPS origin, with a different port from HealthPort.'
}
$null = New-Item -ItemType Directory -Path $root
# Protect the root BEFORE writing secrets. Localized Windows group names are avoided via SIDs.
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-32-544', 'S-1-5-18')) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $root -AclObject $acl
foreach ($dir in @('releases','config','secrets','logs','backups','data/keys','state')) { $null = New-Item -ItemType Directory -Force -Path (Join-Path $root $dir) }

# A virtual service account has no password to distribute. Its path is replaced after preflight.
$placeholder = Get-ServiceCommand (Join-Path $root 'releases/not-installed/app') $root $Environment
& sc.exe create $ServiceName 'binPath=' $placeholder 'start=' 'demand' 'obj=' "NT SERVICE\$ServiceName" 'DisplayName=' "D3Parking ($Environment)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Service creation failed. Initialization is incomplete at $root; no application was started." }
& sc.exe sidtype $ServiceName unrestricted | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not enable the service SID.' }
$serviceSid = ([Security.Principal.NTAccount]::new("NT SERVICE\$ServiceName")).Translate([Security.Principal.SecurityIdentifier])
# Root traversal only. The service cannot read deployment credentials or change binaries/config.
$rootAcl = Get-Acl -LiteralPath $root
$rootAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'ReadAndExecute', 'None', 'None', 'Allow'))
Set-Acl -LiteralPath $root -AclObject $rootAcl
foreach ($dir in @('releases','config')) {
    $path = Join-Path $root $dir
    $folderAcl = Get-Acl -LiteralPath $path
    $folderAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $path -AclObject $folderAcl
}
foreach ($dir in @('logs','data')) {
    $path = Join-Path $root $dir
    $folderAcl = Get-Acl -LiteralPath $path
    $folderAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $path -AclObject $folderAcl
}
$password = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=D3Parking Data Protection', $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(5))
    try { [IO.File]::WriteAllBytes((Join-Path $root 'secrets/protection.pfx'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password)) }
    finally { $certificate.Dispose() }
} finally { $rsa.Dispose() }
$settings = [ordered]@{
    Deployment = @{ Environment = $Environment }
    Account = @{ BaseUrl = $PublicUrl.GetLeftPart([UriPartial]::Authority) }
    AllowedHosts = "$($PublicUrl.Host);127.0.0.1"
    Kestrel = @{ Endpoints = @{
        Public = @{ Url = "https://0.0.0.0:$($PublicUrl.Port)"; Certificate = @{ Path = (Join-Path $root 'secrets/site.pfx') } }
        Health = @{ Url = "http://127.0.0.1:$HealthPort" }
    } }
    DataProtection = @{ Certificate = @{ Path = (Join-Path $root 'secrets/protection.pfx') } }
    Smtp = @{ Host = 'REPLACE_WITH_SMTP_RELAY'; Port = 587; Security = 'StartTls'; Authentication = 'Basic'; SenderEmail = 'REPLACE_WITH_SENDER'; SenderName = 'D3Parking'; TimeoutSeconds = 30 }
}
Write-JsonAtomic $settings (Join-Path $root 'config/appsettings.json')
Write-JsonAtomic ([ordered]@{
    ServiceName = $ServiceName; Environment = $Environment; HealthUrl = "http://127.0.0.1:$HealthPort";
    PublicUrl = $PublicUrl.GetLeftPart([UriPartial]::Authority); SqlBackupDirectory = 'REPLACE_WITH_ABSOLUTE_PATH_ON_SQL_SERVER'; ApprovedMigrations = @()
}) (Join-Path $root 'config/deployment.json')
Write-JsonAtomic ([ordered]@{
    ConnectionStrings = @{ SqlServer = 'Server=REPLACE_SQL_SERVER;Database=D3Parking;User ID=REPLACE_APP_USER;Password=REPLACE_PASSWORD;Encrypt=True;TrustServerCertificate=False' }
    IdentitySeed = @{ AdminEmail = 'REPLACE_WITH_ADMIN_EMAIL'; AdminPassword = 'REPLACE_WITH_UNIQUE_PASSWORD' }
    Smtp = @{ UserName = 'REPLACE_WITH_SMTP_USER'; Password = 'REPLACE_WITH_SMTP_PASSWORD' }
    Kestrel = @{ Endpoints = @{ Public = @{ Certificate = @{ Password = 'REPLACE_WITH_PFX_PASSWORD' } } } }
    DataProtection = @{ Certificate = @{ Password = $password } }
}) (Join-Path $root 'secrets/secrets.json')
Write-JsonAtomic (@{ ConnectionStrings = @{ SqlServer = 'Server=REPLACE_SQL_SERVER;Database=D3Parking;User ID=REPLACE_DEPLOY_USER;Password=REPLACE_PASSWORD;Encrypt=True;TrustServerCertificate=False' } }) (Join-Path $root 'secrets/deployment.json')
# Application-readable secret material lives separately from the administrator-only DB credential.
foreach ($name in @('secrets.json','protection.pfx')) {
    $path = Join-Path $root "secrets/$name"
    $fileAcl = Get-Acl -LiteralPath $path
    $fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'Read', 'Allow'))
    Set-Acl -LiteralPath $path -AclObject $fileAcl
}
$secretAcl = Get-Acl -LiteralPath (Join-Path $root 'secrets')
$secretAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($serviceSid, 'ReadAndExecute', 'None', 'None', 'Allow'))
Set-Acl -LiteralPath (Join-Path $root 'secrets') -AclObject $secretAcl
Write-Host "[OK] Prepared $root and stopped service $ServiceName. No application has been started."
Write-Host 'Next: fill shared settings, copy the HTTPS PFX and grant the service read access as shown in ADMIN-GUIDE.md.'
