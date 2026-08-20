param(
    [ValidateSet('Auto', 'Containers', 'LocalDb', 'External')]
    [string]$Backend = 'Auto',

    [Parameter(Position = 0, Mandatory = $true)]
    [string]$Executable,

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$CommandArguments
)

# Windows mode: run a command with the D3Parking development services configured.
$ErrorActionPreference = 'Stop'

$repoDir = (& git rev-parse --show-toplevel).Trim()
$setupScript = Join-Path $repoDir '.codex/hooks/session-start.ps1'
$envFile = Join-Path $repoDir '.codex/dev.windows.env'

& $setupScript -SkipRestore -Backend $Backend | Out-Null
. $envFile

& $Executable @CommandArguments
exit $LASTEXITCODE
