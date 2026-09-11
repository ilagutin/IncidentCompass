$script:ProductionRepositoryRoot = Split-Path -Parent $PSScriptRoot
$script:ProductionComposeFiles = @(
    (Join-Path $script:ProductionRepositoryRoot "docker-compose.yml"),
    (Join-Path $script:ProductionRepositoryRoot "compose.production.yml")
)
$script:ProductionEnvironmentNames = @(
    "INCIDENTCOMPASS_COMPOSE_PROJECT", "INCIDENTCOMPASS_IMAGE_TAG", "IC_API_PORT", "IC_POSTGRES_PORT",
    "POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD",
    "INCIDENTCOMPASS_API_KEY_AUTH_ENABLED", "INCIDENTCOMPASS_API_KEY_ID", "INCIDENTCOMPASS_TENANT_ID",
    "INCIDENTCOMPASS_API_KEY_SHA256", "INCIDENTCOMPASS_API_KEY_PERMIT_LIMIT", "INCIDENTCOMPASS_API_KEY_WINDOW_SECONDS",
    "INCIDENTCOMPASS_LLM_BASE_URL", "INCIDENTCOMPASS_LLM_CHAT_COMPLETIONS_PATH", "INCIDENTCOMPASS_LLM_MODEL",
    "INCIDENTCOMPASS_LLM_API_KEY", "INCIDENTCOMPASS_EMBEDDINGS_BASE_URL", "INCIDENTCOMPASS_EMBEDDINGS_PATH",
    "INCIDENTCOMPASS_EMBEDDINGS_MODEL", "INCIDENTCOMPASS_EMBEDDINGS_API_KEY",
    "INCIDENTCOMPASS_ALLOW_INSECURE_LOOPBACK_PROVIDER", "INCIDENTCOMPASS_SOURCE_ROOT",
    "INCIDENTCOMPASS_SOURCE_SERVICE", "INCIDENTCOMPASS_SOURCE_RELEASE", "INCIDENTCOMPASS_GITHUB_OWNER",
    "INCIDENTCOMPASS_GITHUB_REPOSITORY", "INCIDENTCOMPASS_GITHUB_TOKEN",
    "INCIDENTCOMPASS_GITHUB_BASE_BRANCH", "INCIDENTCOMPASS_MEMORY_SEED_ENABLED",
    "INCIDENTCOMPASS_MEMORY_SEED_OWNER", "INCIDENTCOMPASS_TELEGRAM_ENABLED", "INCIDENTCOMPASS_TELEGRAM_ROUTE_ID",
    "INCIDENTCOMPASS_TELEGRAM_CHAT_ID", "INCIDENTCOMPASS_TELEGRAM_BOT_TOKEN",
    "INCIDENTCOMPASS_LOG_MAX_SIZE", "INCIDENTCOMPASS_LOG_MAX_FILES"
)

function Resolve-ProductionEnvironmentFile {
    param([Parameter(Mandatory)][string] $Path)

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved.Path -PathType Leaf)) {
        throw "Production environment file does not exist."
    }

    return $resolved.Path
}

function Read-ProductionEnvironment {
    param([Parameter(Mandatory)][string] $Path)

    $values = @{}
    $lineNumber = 0
    foreach ($rawLine in Get-Content -LiteralPath $Path) {
        $lineNumber++
        $line = $rawLine.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith("#", [StringComparison]::Ordinal)) {
            continue
        }

        $separator = $line.IndexOf("=", [StringComparison]::Ordinal)
        if ($separator -lt 1) {
            throw "Production environment file has an invalid entry at line $lineNumber."
        }

        $key = $line.Substring(0, $separator).Trim()
        if ($key -notmatch '^[A-Za-z_][A-Za-z0-9_]*$' -or $values.ContainsKey($key)) {
            throw "Production environment file has an invalid or duplicate key at line $lineNumber."
        }

        $value = $line.Substring($separator + 1).Trim()
        if ($value.Length -ge 2 -and
            (($value[0] -eq '"' -and $value[$value.Length - 1] -eq '"') -or
             ($value[0] -eq "'" -and $value[$value.Length - 1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        $values[$key] = $value
    }

    return $values
}

function Import-ProductionEnvironment {
    param([Parameter(Mandatory)][hashtable] $Values)

    foreach ($name in $script:ProductionEnvironmentNames) {
        if (-not $Values.ContainsKey($name)) {
            [Environment]::SetEnvironmentVariable($name, $null, "Process")
        }
    }
    foreach ($entry in $Values.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, [string] $entry.Value, "Process")
    }
}

function Assert-NoAmbientProductionOverrides {
    param([Parameter(Mandatory)][hashtable] $Values)

    foreach ($name in $script:ProductionEnvironmentNames) {
        $ambient = [Environment]::GetEnvironmentVariable($name, "Process")
        if ($null -ne $ambient -and
            (-not $Values.ContainsKey($name) -or $ambient -cne [string] $Values[$name])) {
            throw "Ambient process environment overrides production setting '$name'. Unset it or make the environment file authoritative."
        }
    }
}

function Get-RequiredProductionValue {
    param(
        [Parameter(Mandatory)][hashtable] $Values,
        [Parameter(Mandatory)][string] $Name
    )

    if (-not $Values.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace([string] $Values[$Name])) {
        throw "Production setting '$Name' is required."
    }

    return [string] $Values[$Name]
}

function Assert-ProductionValueIsConfigured {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Value,
        [string[]] $RejectedValues = @()
    )

    if ($Value -match '(?i)(change[-_ ]?me|example\.invalid|replace[-_ ]?me|your[-_ ])' -or
        $RejectedValues -contains $Value) {
        throw "Production setting '$Name' still uses a shipped default or placeholder."
    }
}

function Test-ProductionBoolean {
    param([Parameter(Mandatory)][string] $Value)

    return $Value -in @("true", "false")
}

function Test-AbsoluteOperatorPath {
    param([Parameter(Mandatory)][string] $Path)

    return [IO.Path]::IsPathRooted($Path) -or $Path -match '^[A-Za-z]:[\\/]'
}

function Remove-ExpiredOwnedBackupPairs {
    param(
        [Parameter(Mandatory)][string] $Directory,
        [Parameter(Mandatory)][int] $Retain
    )

    $pairs = foreach ($manifestFile in Get-ChildItem -LiteralPath $Directory `
        -Filter "incidentcompass-backup-*.manifest.json" -File) {
        try {
            $manifest = Get-Content -Raw -LiteralPath $manifestFile.FullName | ConvertFrom-Json
            if ($manifest.kind -ne "IncidentCompass.PostgresBackup" -or
                $manifest.formatVersion -ne 1 -or
                [string]::IsNullOrWhiteSpace($manifest.dumpFile) -or
                [IO.Path]::GetFileName($manifest.dumpFile) -ne $manifest.dumpFile -or
                $manifest.dumpFile -notmatch '^incidentcompass-backup-[0-9]{8}T[0-9]{9}Z-[0-9a-f]{8}\.dump$' -or
                $manifestFile.Name -cne ($manifest.dumpFile.Substring(0, $manifest.dumpFile.Length - 5) + ".manifest.json")) {
                continue
            }
            $dumpPath = Join-Path $Directory $manifest.dumpFile
            if (-not (Test-Path -LiteralPath $dumpPath -PathType Leaf)) {
                continue
            }
            $dumpLength = (Get-Item -LiteralPath $dumpPath).Length
            if ($manifest.postgresFormat -ne "custom" -or [long] $manifest.sizeBytes -ne $dumpLength) {
                continue
            }
            $actualHash = (Get-FileHash -LiteralPath $dumpPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne $manifest.sha256) {
                continue
            }
            [pscustomobject]@{
                ManifestPath = $manifestFile.FullName
                DumpPath = $dumpPath
                CreatedAtUtc = [DateTimeOffset]::Parse($manifest.createdAtUtc)
            }
        }
        catch {
            continue
        }
    }

    $expired = @($pairs | Sort-Object CreatedAtUtc -Descending | Select-Object -Skip $Retain)
    foreach ($pair in $expired) {
        Remove-Item -LiteralPath $pair.DumpPath -Force
        Remove-Item -LiteralPath $pair.ManifestPath -Force
    }
}

function Get-ProductionComposeArguments {
    param(
        [Parameter(Mandatory)][string] $EnvironmentFile,
        [Parameter(Mandatory)][string] $ProjectName,
        [switch] $RecoveryProfile
    )

    $arguments = @("compose")
    foreach ($composeFile in $script:ProductionComposeFiles) {
        $arguments += @("-f", $composeFile)
    }
    $arguments += @("--env-file", $EnvironmentFile, "--project-name", $ProjectName)
    if ($RecoveryProfile) {
        $arguments += @("--profile", "recovery")
    }
    return $arguments
}

function Invoke-ProductionDocker {
    param(
        [Parameter(Mandatory)][string[]] $ComposeArguments,
        [Parameter(Mandatory)][string[]] $Arguments,
        [switch] $CaptureOutput
    )

    if ($CaptureOutput) {
        $output = & docker @ComposeArguments @Arguments 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "Docker operation failed with exit code $LASTEXITCODE."
        }
        return $output
    }

    & docker @ComposeArguments @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Docker operation failed with exit code $LASTEXITCODE."
    }
}

function Wait-ProductionServiceHealthy {
    param(
        [Parameter(Mandatory)][string[]] $ComposeArguments,
        [Parameter(Mandatory)][string] $Service,
        [int] $TimeoutSeconds = 180
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $containerId = Invoke-ProductionDocker -ComposeArguments $ComposeArguments `
            -Arguments @("ps", "--quiet", $Service) -CaptureOutput
        if (-not [string]::IsNullOrWhiteSpace(($containerId | Select-Object -First 1))) {
            $status = & docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' `
                ($containerId | Select-Object -First 1) 2>$null
            if ($LASTEXITCODE -eq 0 -and $status -eq "healthy") {
                return
            }
            if ($LASTEXITCODE -eq 0 -and $status -in @("exited", "dead", "unhealthy")) {
                throw "Production service '$Service' entered terminal health state '$status'."
            }
        }
        Start-Sleep -Seconds 2
    }

    throw "Production service '$Service' did not become healthy within $TimeoutSeconds seconds."
}

function Invoke-BoundedProductionCommand {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $ArgumentList,
        [Parameter(Mandatory)][int] $TimeoutSeconds
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        $null = $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $null = $process.Start()
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $null = $process.WaitForExitAsync().WaitAsync(
            [TimeSpan]::FromSeconds($TimeoutSeconds)).GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Operation failed with exit code $($process.ExitCode)."
        }
        $null = $stderrTask.GetAwaiter().GetResult()
        return $stdoutTask.GetAwaiter().GetResult()
    }
    catch [TimeoutException] {
        if (-not $process.HasExited) {
            $process.Kill($true)
        }
        throw "Operation exceeded its bounded timeout."
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-BoundedDockerBinaryOutput {
    param(
        [Parameter(Mandatory)][string[]] $ArgumentList,
        [Parameter(Mandatory)][string] $OutputPath,
        [Parameter(Mandatory)][int] $TimeoutSeconds
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "docker"
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        $null = $startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $output = [IO.File]::Create($OutputPath)
    $null = $process.Start()
    try {
        $copyTask = $process.StandardOutput.BaseStream.CopyToAsync($output)
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $waitTask = $process.WaitForExitAsync()
        $null = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]] @($copyTask, $waitTask)).WaitAsync(
            [TimeSpan]::FromSeconds($TimeoutSeconds)).GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "Operation failed with exit code $($process.ExitCode)."
        }
        $null = $stderrTask.GetAwaiter().GetResult()
    }
    catch [TimeoutException] {
        if (-not $process.HasExited) {
            $process.Kill($true)
        }
        throw "Operation exceeded its bounded timeout."
    }
    finally {
        $output.Dispose()
        $process.Dispose()
    }
}
