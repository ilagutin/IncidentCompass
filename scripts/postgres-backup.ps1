param(
    [Parameter(Mandatory)][string] $BackupDirectory,
    [string] $EnvironmentFile = ".env.production",
    [ValidateRange(1, 7200)][int] $TimeoutSeconds = 1800,
    [ValidateRange(1, 100)][int] $RetainSuccessfulBackups = 7
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "production-common.ps1")

try {
    if (-not (Test-AbsoluteOperatorPath $BackupDirectory)) {
        throw "BackupDirectory must be an explicit absolute operator-selected directory."
    }
    if (-not (Test-Path -LiteralPath $BackupDirectory -PathType Container)) {
        throw "BackupDirectory must already exist."
    }
    $backupRoot = (Resolve-Path -LiteralPath $BackupDirectory).Path
    $environmentPath = Resolve-ProductionEnvironmentFile -Path $EnvironmentFile

    & pwsh -NoProfile -File (Join-Path $PSScriptRoot "production-preflight.ps1") `
        -EnvironmentFile $environmentPath
    if ($LASTEXITCODE -ne 0) {
        throw "Production preflight failed."
    }

    $values = Read-ProductionEnvironment -Path $environmentPath
    Import-ProductionEnvironment -Values $values
    $projectName = Get-RequiredProductionValue $values "INCIDENTCOMPASS_COMPOSE_PROJECT"
    $compose = Get-ProductionComposeArguments -EnvironmentFile $environmentPath -ProjectName $projectName
    $containerId = Invoke-ProductionDocker -ComposeArguments $compose `
        -Arguments @("ps", "--quiet", "postgres") -CaptureOutput
    if ([string]::IsNullOrWhiteSpace(($containerId | Select-Object -First 1))) {
        throw "Production PostgreSQL service is not running."
    }
    Wait-ProductionServiceHealthy -ComposeArguments $compose -Service "postgres" -TimeoutSeconds 60

    $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'")
    $suffix = [Guid]::NewGuid().ToString("N").Substring(0, 8)
    $baseName = "incidentcompass-backup-$timestamp-$suffix"
    $partialPath = Join-Path $backupRoot "$baseName.partial"
    $dumpPath = Join-Path $backupRoot "$baseName.dump"
    $manifestPath = Join-Path $backupRoot "$baseName.manifest.json"

    try {
        $dumpArguments = @($compose) + @(
            "exec", "--no-TTY", "postgres", "sh", "-c",
            'PGPASSWORD="$POSTGRES_PASSWORD" exec pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc'
        )
        Invoke-BoundedDockerBinaryOutput -ArgumentList $dumpArguments `
            -OutputPath $partialPath -TimeoutSeconds $TimeoutSeconds
        if ((Get-Item -LiteralPath $partialPath).Length -le 0) {
            throw "PostgreSQL backup produced an empty dump."
        }
        Move-Item -LiteralPath $partialPath -Destination $dumpPath

        $hash = (Get-FileHash -LiteralPath $dumpPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest = [ordered]@{
            kind = "IncidentCompass.PostgresBackup"
            formatVersion = 1
            createdAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
            dumpFile = [IO.Path]::GetFileName($dumpPath)
            sizeBytes = (Get-Item -LiteralPath $dumpPath).Length
            sha256 = $hash
            postgresFormat = "custom"
        }
        $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    }
    catch {
        Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $dumpPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
        throw
    }

    Remove-ExpiredOwnedBackupPairs -Directory $backupRoot -Retain $RetainSuccessfulBackups
    Write-Host "PostgreSQL backup completed with a SHA-256 manifest."
    [pscustomobject]@{ DumpPath = $dumpPath; ManifestPath = $manifestPath; Sha256 = $hash }
}
catch {
    Write-Error -ErrorAction Continue $_.Exception.Message
    exit 1
}
