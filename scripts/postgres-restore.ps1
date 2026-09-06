param(
    [Parameter(Mandatory)][string] $BackupFile,
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
. (Join-Path $PSScriptRoot "production-common.ps1")

try {
    if ($TargetProject -notmatch '^[a-z0-9][a-z0-9_-]{2,62}$') {
        throw "TargetProject is not a valid explicit Compose project name."
    }
    if ($ConfirmTarget -cne $TargetProject) {
        throw "ConfirmTarget must exactly match TargetProject."
    }
    if (-not (Test-AbsoluteOperatorPath $BackupFile) -or
        -not (Test-Path -LiteralPath $BackupFile -PathType Leaf)) {
        throw "BackupFile must be an existing absolute operator-selected dump."
    }
    $dumpPath = (Resolve-Path -LiteralPath $BackupFile).Path
    if ($dumpPath -notmatch '\.dump$') {
        throw "BackupFile must use the code-owned .dump name."
    }
    $manifestPath = $dumpPath.Substring(0, $dumpPath.Length - 5) + ".manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "The matching backup manifest is required."
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.kind -ne "IncidentCompass.PostgresBackup" -or
        $manifest.formatVersion -ne 1 -or
        $manifest.postgresFormat -ne "custom" -or
        $manifest.dumpFile -ne [IO.Path]::GetFileName($dumpPath) -or
        [long] $manifest.sizeBytes -ne (Get-Item -LiteralPath $dumpPath).Length) {
        throw "Backup manifest identity is invalid."
    }
    $actualHash = (Get-FileHash -LiteralPath $dumpPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $manifest.sha256) {
        throw "Backup SHA-256 does not match its manifest."
    }

    $environmentPath = Resolve-ProductionEnvironmentFile -Path $EnvironmentFile
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "production-preflight.ps1") `
        -EnvironmentFile $environmentPath
    if ($LASTEXITCODE -ne 0) {
        throw "Production preflight failed."
    }
    $values = Read-ProductionEnvironment -Path $environmentPath
    Import-ProductionEnvironment -Values $values
    $liveProject = Get-RequiredProductionValue $values "INCIDENTCOMPASS_COMPOSE_PROJECT"
    if ($TargetProject -ceq $liveProject) {
        throw "Restore target must be distinct from the live production project."
    }
    $liveApiPort = if ($values.ContainsKey("IC_API_PORT")) { [int] $values["IC_API_PORT"] } else { 5198 }
    $livePostgresPort = if ($values.ContainsKey("IC_POSTGRES_PORT")) { [int] $values["IC_POSTGRES_PORT"] } else { 5432 }
    if ($TargetApiPort -eq $liveApiPort -or $TargetPostgresPort -eq $livePostgresPort -or
        $TargetApiPort -eq $TargetPostgresPort) {
        throw "Recovery ports must be distinct from the live API/PostgreSQL ports and each other."
    }
    [Environment]::SetEnvironmentVariable("IC_API_PORT", $TargetApiPort.ToString(), "Process")
    [Environment]::SetEnvironmentVariable("IC_POSTGRES_PORT", $TargetPostgresPort.ToString(), "Process")

    $targetVolume = "${TargetProject}_postgres-data"
    $existingContainers = & docker ps --all --quiet `
        --filter "label=com.docker.compose.project=$TargetProject" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not verify the recovery target."
    }
    if (-not [string]::IsNullOrWhiteSpace(($existingContainers -join ""))) {
        throw "Restore target project already has containers."
    }
    & docker volume inspect $targetVolume *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Restore target volume already exists."
    }
    & docker network inspect "${TargetProject}_default" *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Restore target project already has a network."
    }

    if ($WhatIf) {
        Write-Host "WhatIf: validated fresh recovery project '$TargetProject', volume '$targetVolume' and loopback recovery ports."
        return
    }

    $compose = Get-ProductionComposeArguments -EnvironmentFile $environmentPath `
        -ProjectName $TargetProject -RecoveryProfile
    Invoke-ProductionDocker -ComposeArguments $compose -Arguments @("up", "--detach", "postgres-restore")
    Wait-ProductionServiceHealthy -ComposeArguments $compose -Service "postgres-restore" `
        -TimeoutSeconds $HealthTimeoutSeconds

    $emptyQuery = "SELECT count(*) FROM pg_catalog.pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema');"
    $emptyArguments = @($compose) + @(
        "exec", "--no-TTY", "postgres-restore", "sh", "-c",
        "PGPASSWORD=`"`$POSTGRES_PASSWORD`" PGOPTIONS='-c statement_timeout=30000' exec psql -v ON_ERROR_STOP=1 -At -U `"`$POSTGRES_USER`" -d `"`$POSTGRES_DB`" -c `"$emptyQuery`""
    )
    $emptyResult = Invoke-BoundedProductionCommand -FilePath "docker" `
        -ArgumentList $emptyArguments -TimeoutSeconds ([Math]::Min($HealthTimeoutSeconds, 60))
    if ($emptyResult.Trim() -ne "0") {
        throw "Restore target database is not empty. Target was left for inspection."
    }

    Invoke-ProductionDocker -ComposeArguments $compose -Arguments @(
        "cp", $dumpPath, "postgres-restore:/tmp/incidentcompass.restore.dump"
    )
    $restoreArguments = @($compose) + @(
        "exec", "--no-TTY", "postgres-restore", "sh", "-c",
        'PGPASSWORD="$POSTGRES_PASSWORD" exec pg_restore --exit-on-error --no-owner --no-privileges -U "$POSTGRES_USER" -d "$POSTGRES_DB" /tmp/incidentcompass.restore.dump'
    )
    $null = Invoke-BoundedProductionCommand -FilePath "docker" `
        -ArgumentList $restoreArguments -TimeoutSeconds $TimeoutSeconds

    Invoke-ProductionDocker -ComposeArguments $compose -Arguments @("stop", "postgres-restore")
    Invoke-ProductionDocker -ComposeArguments $compose -Arguments @("rm", "--force", "postgres-restore")
    Invoke-ProductionDocker -ComposeArguments $compose -Arguments @("up", "--detach", "postgres", "api")
    foreach ($service in @("postgres", "api")) {
        Wait-ProductionServiceHealthy -ComposeArguments $compose -Service $service `
            -TimeoutSeconds $HealthTimeoutSeconds
    }

    $readbackQuery = @"
SELECT json_build_object(
  'reports', (SELECT count(*) FROM incidentcompass.triage_reports),
  'evidence', (SELECT count(*) FROM incidentcompass.triage_evidence),
  'ledger', (SELECT count(*) FROM incidentcompass.triage_ledger),
  'approvals', (SELECT count(*) FROM incidentcompass.action_approvals),
  'actionProjection', (SELECT count(*) FROM incidentcompass.action_approvals WHERE external_resource_kind IS NOT NULL),
  'memoryItems', (SELECT count(*) FROM incidentcompass.memory_items),
  'memoryChunks', (SELECT count(*) FROM incidentcompass.memory_chunks),
  'migrationHistory', (SELECT count(*) FROM incidentcompass.schema_migrations),
  'failedMigrations', (SELECT count(*) FROM incidentcompass.schema_migrations WHERE status <> 'Applied')
)::text;
"@.Trim()
    $readbackArguments = @($compose) + @(
        "exec", "--no-TTY", "postgres", "sh", "-c",
        "PGPASSWORD=`"`$POSTGRES_PASSWORD`" PGOPTIONS='-c statement_timeout=30000' exec psql -v ON_ERROR_STOP=1 -At -U `"`$POSTGRES_USER`" -d `"`$POSTGRES_DB`" -c `"$readbackQuery`""
    )
    $readback = Invoke-BoundedProductionCommand -FilePath "docker" `
        -ArgumentList $readbackArguments -TimeoutSeconds ([Math]::Min($HealthTimeoutSeconds, 60))
    $summary = ($readback | ConvertFrom-Json)
    if ($summary.failedMigrations -ne 0 -or $summary.migrationHistory -le 0) {
        throw "Restored migration history did not pass bounded readback. Target was left for inspection."
    }

    Write-Host "Restore completed with PostgreSQL and API healthy, bounded recovery readback passed, and Worker intentionally stopped for operator inspection."
    Write-Output $summary
}
catch {
    Write-Error -ErrorAction Continue $_.Exception.Message
    exit 1
}
