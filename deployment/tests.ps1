#requires -Version 7.4
# Native assertions: no Pester/network/service/admin dependency.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'D3Parking.ps1')
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
function Assert-Test([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAILED: $Name" }
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
    $password = 'fixture;"quoted'' password'
    $connection = Read-SqlConnection (New-SqlConnection 'sql.example.test,1433' parking runtime $password)
    Assert-Test ($connection.Password -ceq $password -and $connection.Server -eq 'sql.example.test,1433' -and $connection.User -eq 'runtime') 'SQL credentials survive quoting and round trip'
    $appSettings = '{"Smtp":{"Host":"old"},"DataProtection":{"Certificate":{"Password":"fixture-secret"}},"Extension":{"Value":7}}' | ConvertFrom-Json -AsHashtable
    $secretSettings = '{"Smtp":{"Host":"effective","Password":"fixture-smtp"}}' | ConvertFrom-Json -AsHashtable
    Normalize-ManagedSettings $appSettings $secretSettings
    Assert-Test ((Get-Setting $appSettings 'Smtp:Host') -eq 'effective' -and (Get-Setting $secretSettings 'Smtp:Host') -eq '' -and (Get-Setting $secretSettings 'DataProtection:Certificate:Password') -eq 'fixture-secret' -and (Get-Setting $appSettings 'DataProtection:Certificate:Password') -eq '' -and $appSettings.Extension.Value -eq 7) 'Configuration normalization preserves effective values and unknown extensions'
    Assert-Test (Confirm-Operation 'Automated confirmation fixture' -Yes) 'Explicit Yes confirms the reviewed plan'
    & {
        function Read-Host { return '' }
        Assert-Test (-not (Confirm-Operation 'Default confirmation fixture')) 'Enter cancels changes by default'
    }
    Assert-Test ((Get-FriendlyError 'Unexpected JSON: fixture-super-secret') -notmatch 'fixture-super-secret') 'Unknown parser errors never echo secrets'
    foreach ($unsupported in @('Install','Configure','Certificate','Restart','RestoreConfiguration')) {
        Expect-Failure { Invoke-WizardAction -Selected $unsupported -Root 'does-not-exist' -CheckOnly } "CheckOnly rejects mutating action $unsupported before touching files"
    }
    $standalone = Join-Path $testRoot 'standalone.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'D3Parking.ps1') -Destination $standalone
    $help = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File $standalone -Action Help | Out-String
    Assert-Test ($LASTEXITCODE -eq 0 -and $help.Contains('jeden provozní PowerShell skript')) 'Copied single script runs without dependencies'
    & {
        # Only external service/maintenance/ACL boundaries are simulated. Paths, ZIP validation,
        # deployment orchestration, locks, journals and configuration transactions are real.
        function Assert-Administrator {}
        function Assert-ServiceAccess {}
        function New-ProtectedDirectory([string]$Path) { $null = New-Item -ItemType Directory -Path $Path }
        function Get-CimInstance { return [pscustomobject]@{ StartName='NT SERVICE\D3Parking'; State='Running'; StartMode='Auto'; DelayedAutoStart=$true; PathName=(Get-ServiceCommand (Join-Path $script:fixtureRoot 'releases/1.0.0/app') $script:fixtureRoot Production) } }
        function Get-Service {
            [CmdletBinding()]param([string]$Name)
            $service = [pscustomobject]@{ Status='Running' }
            $service | Add-Member ScriptMethod WaitForStatus { param($Status,$Timeout) }
            return $service
        }
        function Stop-Service { [CmdletBinding()]param([string]$Name); $script:stopCount++ }
        function Start-Service { [CmdletBinding()]param([string]$Name); $script:startCount++ }
        function Set-ReleaseService([string]$Name,[string]$BinaryPath) { $script:lastBinary=$BinaryPath }
        function sc.exe { $script:startupMode = $args[-1]; $global:LASTEXITCODE=0 }
        function Wait-ReleaseHealthy([string]$Url,$Manifest,[string]$Environment) {
            if ($script:scenario -in @('start-fails','schema-changed') -and $Manifest.version -eq '1.1.0') { throw 'Health check failed (fixture)' }
        }
        function Invoke-ReleaseCommand([string]$AppPath,[string]$Root,[string]$Environment,[string]$Command,[string]$ReportPath,[string]$ExpectedSchema='') {
            $manifest = Read-Json (Join-Path (Split-Path $AppPath) 'release.json')
            if ($Command -eq 'upgrade') {
                if ($script:startupMode -ne 'demand') { throw 'Automatic startup must be disabled before database maintenance.' }
                $script:upgradeCount++
                if ($script:scenario -eq 'unknown-upgrade') { throw 'Maintenance failed before report' }
                $result = @{ success=($script:scenario -ne 'backup-fails'); phase='database backup'; reason='fixture'; databaseMayHaveChanged=($script:scenario -eq 'schema-changed'); backup='D:\SqlBackups\fixture.bak' }
            } else {
                $result = @{ success=($script:scenario -ne 'config-fails'); phase='configuration'; reason='fixture'; release=@{version=$manifest.version;commit=$manifest.commit}; schema=@{ applied=@('M1'); pending=@(); target=$manifest.schema }; unapprovedMigrations=@() }
            }
            Write-JsonAtomic $result $ReportPath
            return ($result | ConvertTo-Json -Depth 10 | ConvertFrom-Json)
        }
        function New-DeploymentFixture {
            $script:fixtureRoot = Join-Path $testRoot ([Guid]::NewGuid().ToString('N'))
            foreach ($dir in @('state','logs','releases','config','secrets','data/keys','backups')) { $null = New-Item -ItemType Directory -Force -Path (Join-Path $script:fixtureRoot $dir) }
            foreach ($v in @('1.0.0','1.1.0')) {
                $destination = if ($v -eq '1.0.0') { Join-Path $script:fixtureRoot "releases/$v" } else { Join-Path $script:fixtureRoot 'payload' }
                foreach ($name in $required) {
                    $file = Join-Path $destination $name
                    $null = New-Item -ItemType Directory -Force -Path (Split-Path $file)
                    Set-Content -LiteralPath $file -Value "fixture $v"
                }
                $inventory = @($required | ForEach-Object { @{path=$_;sha256=(Get-FileHash (Join-Path $destination $_)).Hash} })
                Write-JsonAtomic @{format=1;runtime='win-x64';version=$v;commit='fixture';dirty=$false;schema='M1';files=$inventory} (Join-Path $destination 'release.json')
            }
            $script:fixtureZip = Join-Path $script:fixtureRoot 'update.zip'
            [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $script:fixtureRoot 'payload'), $script:fixtureZip)
            $script:fixtureHash = (Get-FileHash $script:fixtureZip).Hash
            Write-JsonAtomic @{ServiceName='D3Parking';Environment='Production';PublicUrl='https://parking.example.test:8443';HealthUrl='http://127.0.0.1:5081';SqlBackupDirectory='D:\SqlBackups';ApprovedMigrations=@()} (Join-Path $script:fixtureRoot 'config/deployment.json')
            Write-JsonAtomic @{Deployment=@{Environment='Production'};Account=@{BaseUrl='https://parking.example.test:8443'};Kestrel=@{Endpoints=@{Health=@{Url='http://127.0.0.1:5081'};Public=@{Certificate=@{Path='unused'}}}};DataProtection=@{Certificate=@{Path='unused'}}} (Join-Path $script:fixtureRoot 'config/appsettings.json')
            foreach ($file in @('secrets/secrets.json','secrets/deployment.json')) { Write-JsonAtomic @{fixture='original'} (Join-Path $script:fixtureRoot $file) }
            Write-JsonAtomic @{current='1.0.0';previous=$null;schema='M1'} (Join-Path $script:fixtureRoot 'state/installation.json')
            $script:stopCount=0; $script:startCount=0; $script:upgradeCount=0; $script:lastBinary=''; $script:scenario='success'; $script:startupMode='delayed-auto'
        }
        New-DeploymentFixture
        Invoke-Deployment -InstallPath $script:fixtureRoot -ReleasePath $script:fixtureZip -ExpectedSha256 $script:fixtureHash -CheckOnly
        Assert-Test ($script:stopCount -eq 0 -and $script:upgradeCount -eq 0 -and -not (Test-Path (Join-Path $script:fixtureRoot 'releases/1.1.0'))) 'Full CheckOnly preserves service, database and release directories'
        & {
            function Confirm-Operation { return $false }
            Invoke-Deployment -InstallPath $script:fixtureRoot -ReleasePath $script:fixtureZip -ExpectedSha256 $script:fixtureHash
        }
        Assert-Test ($script:stopCount -eq 0 -and $script:upgradeCount -eq 0 -and -not (Test-Path (Join-Path $script:fixtureRoot 'state/in-progress.json'))) 'Cancelled deployment creates no operation journal and never stops service'
        Invoke-Deployment -InstallPath $script:fixtureRoot -ReleasePath $script:fixtureZip -ExpectedSha256 $script:fixtureHash -Yes
        Assert-Test ((Read-Json (Join-Path $script:fixtureRoot 'state/installation.json')).current -eq '1.1.0' -and $script:upgradeCount -eq 1 -and $script:startCount -eq 1 -and -not (Test-Path (Join-Path $script:fixtureRoot 'state/in-progress.json'))) 'Successful deployment commits state only after startup verification'
        Assert-Test ($script:startupMode -eq 'delayed-auto') 'Automatic startup restored after successful deployment'
        foreach ($failure in @('start-fails','backup-fails','schema-changed','unknown-upgrade')) {
            New-DeploymentFixture
            $script:scenario=$failure
            Expect-Failure { Invoke-Deployment -InstallPath $script:fixtureRoot -ReleasePath $script:fixtureZip -ExpectedSha256 $script:fixtureHash -Yes } "Deployment reports $failure"
            $journalExists = Test-Path (Join-Path $script:fixtureRoot 'state/in-progress.json')
            if ($failure -in @('start-fails','backup-fails')) {
                Assert-Test (-not $journalExists -and $script:lastBinary.Contains('1.0.0')) "Previous application restored safely after $failure"
            } else {
                Assert-Test ($journalExists -and -not $script:lastBinary.Contains('1.0.0') -and $script:startupMode -eq 'demand') "Older release never started automatically or manually when database changed or result unknown: $failure"
            }
        }
        New-DeploymentFixture
        Write-JsonAtomic @{phase='interrupted'} (Join-Path $script:fixtureRoot 'state/config-in-progress.json')
        Expect-Failure { Invoke-Deployment -InstallPath $script:fixtureRoot -ReleasePath $script:fixtureZip -ExpectedSha256 $script:fixtureHash -Yes } 'Interrupted configuration blocks deployment'
        Assert-Test ($script:stopCount -eq 0 -and $script:upgradeCount -eq 0) 'Configuration journal block occurs before service or database changes'
        Remove-Item -LiteralPath (Join-Path $script:fixtureRoot 'state/config-in-progress.json')
        $configFiles = @('config/appsettings.json','config/deployment.json','secrets/secrets.json','secrets/deployment.json')
        $documents=@{}; foreach ($file in $configFiles) { $documents[$file]=@{fixture='new'} }
        Save-Configuration $script:fixtureRoot $documents
        Assert-Test ((Read-Map (Join-Path $script:fixtureRoot 'secrets/secrets.json')).fixture -eq 'new' -and -not (Test-Path (Join-Path $script:fixtureRoot 'state/config-in-progress.json'))) 'Configuration transaction saves all files and closes journal'
        $script:realWrite = ${function:Write-JsonAtomic}
        & {
            $script:failWrites=$true
            function Write-JsonAtomic($Value,[string]$Path) {
                if ($script:failWrites -and $Path.EndsWith('secrets.json')) { throw 'Injected disk failure' }
                & $script:realWrite $Value $Path
            }
            foreach ($file in $configFiles) { $documents[$file]=@{fixture='partial'} }
            Expect-Failure { Save-Configuration $script:fixtureRoot $documents D3Parking } 'Configuration write failure is reported and recovery journal retained'
            Assert-Test ($script:startupMode -eq 'demand') 'Interrupted configuration disables automatic startup across server reboot'
            $script:failWrites=$false
            Assert-Test (Test-Path (Join-Path $script:fixtureRoot 'state/config-in-progress.json')) 'Interrupted configuration journal survives failed automatic restore'
            # Policy is deliberately unreadable: recovery must not depend on valid current JSON.
            Set-Content -LiteralPath (Join-Path $script:fixtureRoot 'config/deployment.json') -Value '{broken'
            Invoke-WizardAction -Selected RestoreConfiguration -Root $script:fixtureRoot -Yes
            Assert-Test (@($configFiles | Where-Object { (Read-Map (Join-Path $script:fixtureRoot $_)).fixture -ne 'new' }).Count -eq 0) 'Explicit recovery restores all four files even with corrupt current policy'
            Assert-Test ($script:startupMode -eq 'delayed-auto') 'Configuration recovery restores the original service startup mode'
        }
        & {
            function Invoke-RestMethod { return @{status='ready';release=@{version='wrong';environment='Production'}} }
            Assert-Test (-not (Get-HealthResult 'http://127.0.0.1:5081' @{current='1.0.0'} Production).ready) 'Health response from a different version is never reported ready'
        }
        New-DeploymentFixture
        & {
            $script:certificateFixture = Join-Path $script:fixtureRoot 'input.pfx'
            Set-Content -LiteralPath $script:certificateFixture -Value 'mock certificate (certificate validation is a maintenance boundary)'
            function Read-Value([string]$Label,[string]$Default,[scriptblock]$Validate,[string]$Hint) {
                switch -Wildcard ($Label) {
                    'HTTPS certifikát*' { return $script:certificateFixture }
                    'SQL server*' { return 'sql.example.test' }
                    'SQL účet aplikace*' { return 'runtime' }
                    'SQL účet nasazení*' { return 'deployment' }
                    'SMTP relay*' { return 'smtp.example.test' }
                    'Adresa odesílatele*' { return 'parking@example.test' }
                    'SMTP uživatel*' { return 'parking@example.test' }
                    default { return $Default }
                }
            }
            function Read-Secret { return 'Fixtur3!password' }
            function Install-HttpsCertificate([string]$Root,[string]$ServiceName,[string]$Source) {
                $target = Join-Path $Root 'secrets/site-fixture.pfx'
                Copy-Item -LiteralPath $Source -Destination $target -Force
                return $target
            }
            $before=@{}; foreach ($file in $configFiles) { $before[$file]=(Get-FileHash (Join-Path $script:fixtureRoot $file)).Hash }
            $script:scenario='config-fails'
            Expect-Failure { Invoke-ConfigurationWizard $script:fixtureRoot (Join-Path $script:fixtureRoot 'releases/1.0.0/app') -Yes } 'Invalid configuration candidate is rejected before saving'
            Assert-Test (@($configFiles | Where-Object { (Get-FileHash (Join-Path $script:fixtureRoot $_)).Hash -ne $before[$_] }).Count -eq 0) 'Candidate preflight failure preserves all original files'
            $script:scenario='success'
            & {
                function Confirm-Operation { return $false }
                Assert-Test (-not (Invoke-ConfigurationWizard $script:fixtureRoot (Join-Path $script:fixtureRoot 'releases/1.0.0/app'))) 'Configuration cancellation returns without saving'
            }
            Assert-Test (@($configFiles | Where-Object { (Get-FileHash (Join-Path $script:fixtureRoot $_)).Hash -ne $before[$_] }).Count -eq 0) 'Cancelled configuration preserves original files'
            $output = Invoke-ConfigurationWizard $script:fixtureRoot (Join-Path $script:fixtureRoot 'releases/1.0.0/app') -Yes *>&1 | Out-String
            $saved = Read-Map (Join-Path $script:fixtureRoot 'secrets/secrets.json')
            Assert-Test ((Read-SqlConnection (Get-Setting $saved 'ConnectionStrings:SqlServer')).Password -eq 'Fixtur3!password') 'Complete wizard saves validated SQL configuration'
            Assert-Test (-not $output.Contains('Fixtur3!password')) 'Wizard summary never prints credentials'
            Assert-Test ($script:stopCount -eq 0 -and $script:upgradeCount -eq 0) 'Configuration wizard never stops service or upgrades database'
            Assert-Test (@(Get-ChildItem (Join-Path $script:fixtureRoot 'backups') -Filter 'candidate-*').Count -eq 0) 'Protected configuration candidates removed after failure, cancellation and success'
        }
    }
    Write-Host "Deployment checks passed: $passed (service, maintenance process and privileged ACL operations simulated)"
} finally {
    $safe = Assert-ChildPath ([IO.Path]::GetTempPath()) $testRoot
    Assert-NoReparsePoint $safe
    Remove-Item -LiteralPath $safe -Recurse -Force
}
