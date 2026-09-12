param(
    [Parameter(Mandatory)][string] $BackupDirectory,
    [Parameter(Mandatory)][string] $TargetProject,
    [Parameter(Mandatory)][string] $ConfirmTarget,
    [string] $EnvironmentFile = ".env.production",
    [ValidateRange(1, 65535)][int] $TargetApiPort = 15198,
    [ValidateRange(1, 65535)][int] $TargetPostgresPort = 15432,
    [ValidateRange(1, 7200)][int] $TimeoutSeconds = 1800,
    [ValidateRange(30, 900)][int] $HealthTimeoutSeconds = 180,
    [switch] $WhatIf
)

$ErrorActionPreference = "Stop"

if ($WhatIf) {
    $candidate = Get-ChildItem -LiteralPath $BackupDirectory -Filter "incidentcompass-backup-*.dump" -File |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $candidate) {
        throw "WhatIf requires one existing code-owned backup pair in BackupDirectory."
    }
    & (Join-Path $PSScriptRoot "postgres-restore.ps1") -BackupFile $candidate.FullName `
        -TargetProject $TargetProject -ConfirmTarget $ConfirmTarget -EnvironmentFile $EnvironmentFile `
        -TargetApiPort $TargetApiPort -TargetPostgresPort $TargetPostgresPort `
        -TimeoutSeconds $TimeoutSeconds -HealthTimeoutSeconds $HealthTimeoutSeconds -WhatIf
    return
}

$backup = & (Join-Path $PSScriptRoot "postgres-backup.ps1") -BackupDirectory $BackupDirectory `
    -EnvironmentFile $EnvironmentFile -TimeoutSeconds $TimeoutSeconds | Select-Object -Last 1
if ($null -eq $backup -or [string]::IsNullOrWhiteSpace($backup.DumpPath)) {
    throw "Recovery smoke did not receive a completed backup."
}

& (Join-Path $PSScriptRoot "postgres-restore.ps1") -BackupFile $backup.DumpPath `
    -TargetProject $TargetProject -ConfirmTarget $ConfirmTarget -EnvironmentFile $EnvironmentFile `
    -TargetApiPort $TargetApiPort -TargetPostgresPort $TargetPostgresPort `
    -TimeoutSeconds $TimeoutSeconds -HealthTimeoutSeconds $HealthTimeoutSeconds
