param(
    [switch]$SkipRestore,

    [ValidateSet('Auto', 'Containers', 'LocalDb', 'External')]
    [string]$Backend = $env:D3PARKING_DEV_BACKEND
)

# Windows mode: prepare the D3Parking toolchain and local services for Codex.
$ErrorActionPreference = 'Stop'

$repoDir = (& git rev-parse --show-toplevel).Trim()
if (-not $repoDir) {
    throw 'Could not locate the repository root.'
}
Set-Location $repoDir

function Write-SetupLog([string]$Message) {
    [Console]::Error.WriteLine("[codex-setup:windows] $Message")
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 10 SDK is not installed or is not on PATH.'
}

$dotnetVersion = (& dotnet --version).Trim()
if ([int]$dotnetVersion.Split('.')[0] -ne 10) {
    throw "D3Parking requires .NET 10; found $dotnetVersion."
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if ([string]::IsNullOrWhiteSpace($Backend)) {
    $Backend = 'Auto'
}

function Test-DockerAvailable {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        return $false
    }
    & docker info *> $null
    return $LASTEXITCODE -eq 0
}

function Find-LocalDbExecutable {
    $command = Get-Command sqllocaldb -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $pattern = Join-Path $env:ProgramFiles 'Microsoft SQL Server\*\Tools\Binn\SqlLocalDB.exe'
        $candidate = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    return $null
}

$localDbExecutable = Find-LocalDbExecutable
$externalConnection = $env:D3PARKING_SQL_CONNECTION
if ($Backend -eq 'Auto') {
    if (-not [string]::IsNullOrWhiteSpace($externalConnection)) {
        $Backend = 'External'
    } elseif ($localDbExecutable) {
        $Backend = 'LocalDb'
    } elseif (Test-DockerAvailable) {
        $Backend = 'Containers'
    } else {
        throw 'No SQL backend is available. Install SQL Server LocalDB or set D3PARKING_SQL_CONNECTION.'
    }
}

$mssqlConnection = $null
$smtpPort = $env:Smtp__Port

switch ($Backend) {
    'LocalDb' {
        if (-not $localDbExecutable) {
            throw 'LocalDb mode requires SQL Server Express LocalDB (sqllocaldb.exe).'
        }
        & $localDbExecutable info MSSQLLocalDB *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-SetupLog 'Creating the MSSQLLocalDB instance...'
            & $localDbExecutable create MSSQLLocalDB *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Could not create MSSQLLocalDB.' }
        }
        & $localDbExecutable start MSSQLLocalDB *> $null
        if ($LASTEXITCODE -ne 0) { throw 'Could not start MSSQLLocalDB.' }
        $mssqlConnection = 'Server=(localdb)\MSSQLLocalDB;Database=D3Parking;Trusted_Connection=True;TrustServerCertificate=True'
        Write-SetupLog 'Using SQL Server LocalDB; Docker is not required.'
    }
    'External' {
        if ([string]::IsNullOrWhiteSpace($externalConnection)) {
            throw 'External mode requires D3PARKING_SQL_CONNECTION.'
        }
        $mssqlConnection = $externalConnection
        Write-SetupLog 'Using the externally managed SQL Server connection.'
    }
    'Containers' {
        if (-not (Test-DockerAvailable)) {
            throw 'Container mode requires Docker Desktop in Linux container mode.'
        }

        $mssqlContainer = 'd3parking-mssql'
        $mssqlPassword = 'D3Parking!Passw0rd'
        $mssqlConnection = "Server=localhost,1433;Database=D3Parking;User Id=sa;Password=$mssqlPassword;TrustServerCertificate=True"
        $smtpContainer = 'd3parking-smtp'

        & docker container inspect $mssqlContainer *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-SetupLog 'Creating SQL Server 2022 Developer container with Docker Desktop...'
            & docker run -d --name $mssqlContainer `
                --restart unless-stopped `
                -e ACCEPT_EULA=Y `
                -e MSSQL_PID=Developer `
                -e "MSSQL_SA_PASSWORD=$mssqlPassword" `
                -p 127.0.0.1:1433:1433 `
                mcr.microsoft.com/mssql/server:2022-latest *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Could not create the SQL Server container.' }
        } else {
            & docker start $mssqlContainer *> $null
            Write-SetupLog 'Reusing the existing SQL Server container.'
        }

        Write-SetupLog 'Waiting for SQL Server...'
        $mssqlReady = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            & docker exec $mssqlContainer /opt/mssql-tools18/bin/sqlcmd `
                -S localhost -U sa -P $mssqlPassword -C -Q 'SELECT 1' *> $null
            if ($LASTEXITCODE -eq 0) {
                $mssqlReady = $true
                break
            }
            Start-Sleep -Seconds 2
        }

        if (-not $mssqlReady) {
            & docker logs --tail 80 $mssqlContainer | ForEach-Object { [Console]::Error.WriteLine($_) }
            throw 'SQL Server did not become ready within 60 seconds.'
        }

        & docker container inspect $smtpContainer *> $null
        if ($LASTEXITCODE -ne 0) {
            Write-SetupLog 'Creating smtp4dev mail catcher with Docker Desktop...'
            & docker run -d --name $smtpContainer `
                --restart unless-stopped `
                -p 127.0.0.1:2525:25 `
                -p 127.0.0.1:5000:80 `
                docker.io/rnwood/smtp4dev:latest *> $null
            if ($LASTEXITCODE -ne 0) { throw 'Could not create the smtp4dev container.' }
        } else {
            & docker start $smtpContainer *> $null
        }
        $smtpPort = '2525'
    }
}

# The file is ignored by *.env and is dot-sourced by the Windows wrapper.
$escapedConnection = $mssqlConnection.Replace("'", "''")
$windowsEnv = @(
    "`$env:ConnectionStrings__SqlServer = '$escapedConnection'"
    "`$env:D3PARKING_DESIGN_CONNECTION = '$escapedConnection'"
)
if (-not [string]::IsNullOrWhiteSpace($smtpPort)) {
    $windowsEnv += "`$env:Smtp__Port = '$smtpPort'"
}
Set-Content -LiteralPath '.codex/dev.windows.env' -Value $windowsEnv -Encoding utf8

if (-not $SkipRestore) {
    Write-SetupLog 'Restoring local .NET tools and NuGet packages...'
    & dotnet tool restore | ForEach-Object { [Console]::Error.WriteLine($_) }
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }
    & dotnet restore D3Parking.slnx | ForEach-Object { [Console]::Error.WriteLine($_) }
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
}

Write-Output "D3Parking Windows development environment is ready (backend: $Backend)."
Write-Output 'For app, EF, or test commands, use `.codex/run-with-dev-env.ps1`.'
Write-Output 'The helper exports the selected SQL settings; Docker is optional.'
