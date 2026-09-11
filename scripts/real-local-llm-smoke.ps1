param(
    [string] $BaseUrl = "http://host.docker.internal:1234",
    [string] $ChatCompletionsPath = "/v1/chat/completions",
    [string] $Model = "local-model",
    [string] $ApiKey = "local-evaluation-key",
    [string] $EmbeddingBaseUrl = "http://host.docker.internal:1234",
    [string] $EmbeddingModel = "local-embedding-model",
    [string] $EmbeddingApiKey = "local-evaluation-key",
    [int] $Runs = 3,
    [int] $ProviderTimeoutSeconds = 420,
    [int] $AttemptTimeoutSeconds = 660,
    [string] $ResultPath = "artifacts\evaluation\triage-evaluation-result-v3.json"
)

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "scripts/real-local-llm-smoke.ps1 requires PowerShell 7 or later. Run it with pwsh, not Windows PowerShell powershell.exe. No evaluation resources were created."
}

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$previousLocation = Get-Location
$composeProject = ("incidentcompass-eval-" + $PID + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)).ToLowerInvariant()
$composeArgs = @("compose", "-p", $composeProject, "-f", "docker-compose.yml", "-f", "compose.evaluation.yml", "--profile", "demo")

function Invoke-EvaluationCompose {
    param([string[]] $Arguments)

    & docker @composeArgs @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Save-ProcessEnvironment {
    param([string[]] $Names)

    $snapshot = @{}
    foreach ($name in $Names) {
        $value = [Environment]::GetEnvironmentVariable($name, "Process")
        $snapshot[$name] = [pscustomobject]@{ Exists = $null -ne $value; Value = $value }
    }
    return $snapshot
}

function Restore-ProcessEnvironment {
    param([hashtable] $Snapshot)

    foreach ($entry in $Snapshot.GetEnumerator()) {
        $value = if ($entry.Value.Exists) { $entry.Value.Value } else { $null }
        [Environment]::SetEnvironmentVariable($entry.Key, $value, "Process")
    }
}

function Resolve-EvaluationResultPath {
    param(
        [string] $Path,
        [string] $RepositoryRoot
    )

    $candidate = if ([System.IO.Path]::IsPathFullyQualified($Path)) {
        $Path
    }
    else {
        Join-Path $RepositoryRoot $Path
    }
    $absolutePath = [System.IO.Path]::GetFullPath($candidate)
    if (Test-Path -LiteralPath $absolutePath -PathType Container) {
        throw "ResultPath must name a result file, but it resolves to a directory: $absolutePath"
    }
    $directory = Split-Path -Parent $absolutePath
    $leaf = Split-Path -Leaf $absolutePath
    if ([string]::IsNullOrWhiteSpace($directory) -or [string]::IsNullOrWhiteSpace($leaf)) {
        throw "ResultPath must resolve to a file with a parent directory."
    }
    return [pscustomobject]@{ AbsolutePath = $absolutePath; Directory = $directory; Leaf = $leaf }
}

function Get-EvaluationContentIdentity {
    param([string] $HeadRevision)

    $statusLines = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect the evaluated working tree."
    }

    if ($statusLines.Count -eq 0) {
        $tree = (& git rev-parse "HEAD^{tree}").Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "Could not resolve the evaluated Git tree."
        }
        return [pscustomobject]@{ Dirty = $false; Identity = "git-tree:$tree" }
    }

    $material = [System.Text.StringBuilder]::new()
    [void] $material.AppendLine("head:$HeadRevision")
    foreach ($line in $statusLines) {
        [void] $material.AppendLine("status:$line")
    }
    foreach ($line in @(& git diff --binary HEAD -- .)) {
        [void] $material.AppendLine("diff:$line")
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Could not hash tracked evaluation content."
    }
    foreach ($path in @(& git -c core.quotepath=false ls-files --others --exclude-standard | Sort-Object)) {
        $fileHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        [void] $material.AppendLine("untracked:$path`:$fileHash")
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Could not hash untracked evaluation content."
    }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($material.ToString())
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($bytes)
    }
    finally {
        $sha256.Dispose()
    }
    $identity = (($hash | ForEach-Object { $_.ToString("x2") }) -join "")
    return [pscustomobject]@{ Dirty = $true; Identity = "dirty-sha256:$identity" }
}

try {
    Set-Location $repoRoot
    if ($Runs -ne 3) {
        throw "Runs must be exactly 3 for the version 1 evaluation contract."
    }
    if ($ProviderTimeoutSeconds -le 0) {
        throw "ProviderTimeoutSeconds must be positive."
    }
    if ($AttemptTimeoutSeconds -le 0) {
        throw "AttemptTimeoutSeconds must be positive."
    }

    $configurationPath = Join-Path $repoRoot "evaluations\triage\incidentcompass.config.json"
    $configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
    if (@($configuration.Actions.AllowedTools).Count -ne 0 -or $configuration.Actions.DefaultMode -ne "disabled") {
        throw "The evaluation configuration must have empty action grants and disabled action mode."
    }
    $wallClockSeconds = [int] $configuration.Orchestrator.Budget.MaxWallClockSeconds
    if ($ProviderTimeoutSeconds -ge $wallClockSeconds) {
        throw "ProviderTimeoutSeconds must be lower than the evaluation investigation wall-clock budget ($wallClockSeconds seconds)."
    }
    if ($AttemptTimeoutSeconds -le $wallClockSeconds) {
        throw "AttemptTimeoutSeconds must exceed the evaluation investigation wall-clock budget ($wallClockSeconds seconds)."
    }

    $resolvedResult = Resolve-EvaluationResultPath -Path $ResultPath -RepositoryRoot $repoRoot
    $absoluteResultPath = $resolvedResult.AbsolutePath
    $resultDirectory = $resolvedResult.Directory
    $resultLeaf = $resolvedResult.Leaf
    New-Item -ItemType Directory -Force -Path $resultDirectory | Out-Null
    $revision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve the evaluated Git revision."
    }
    $contentIdentity = Get-EvaluationContentIdentity $revision

    $names = @(
        "INCIDENTCOMPASS_LLM_BASE_URL",
        "INCIDENTCOMPASS_LLM_CHAT_COMPLETIONS_PATH",
        "INCIDENTCOMPASS_LLM_MODEL",
        "INCIDENTCOMPASS_LLM_API_KEY",
        "INCIDENTCOMPASS_EMBEDDINGS_BASE_URL",
        "INCIDENTCOMPASS_EMBEDDINGS_MODEL",
        "INCIDENTCOMPASS_EMBEDDINGS_API_KEY",
        "INCIDENTCOMPASS_EVALUATION_PROVIDER_TIMEOUT_SECONDS",
        "INCIDENTCOMPASS_EVALUATION_ATTEMPT_TIMEOUT_SECONDS",
        "INCIDENTCOMPASS_EVALUATION_ARTIFACTS_DIRECTORY",
        "INCIDENTCOMPASS_TESTER_EVALUATION_OUTPUT",
        "INCIDENTCOMPASS_TESTER_EVALUATION_RUNS",
        "INCIDENTCOMPASS_TESTER_EVALUATION_REVISION",
        "INCIDENTCOMPASS_TESTER_EVALUATION_CONTENT_IDENTITY",
        "INCIDENTCOMPASS_TESTER_EVALUATION_CONTENT_DIRTY",
        "INCIDENTCOMPASS_TESTER_EVALUATION_CONFIG"
    )
    $saved = Save-ProcessEnvironment $names
    $evaluationError = $null
    $cleanupError = $null
    $evaluationStartedAtUtc = [DateTimeOffset]::UtcNow
    try {
        $env:INCIDENTCOMPASS_LLM_BASE_URL = $BaseUrl
        $env:INCIDENTCOMPASS_LLM_CHAT_COMPLETIONS_PATH = $ChatCompletionsPath
        $env:INCIDENTCOMPASS_LLM_MODEL = $Model
        $env:INCIDENTCOMPASS_LLM_API_KEY = $ApiKey
        $env:INCIDENTCOMPASS_EMBEDDINGS_BASE_URL = $EmbeddingBaseUrl
        $env:INCIDENTCOMPASS_EMBEDDINGS_MODEL = $EmbeddingModel
        $env:INCIDENTCOMPASS_EMBEDDINGS_API_KEY = $EmbeddingApiKey
        $env:INCIDENTCOMPASS_EVALUATION_PROVIDER_TIMEOUT_SECONDS = $ProviderTimeoutSeconds.ToString()
        $env:INCIDENTCOMPASS_EVALUATION_ATTEMPT_TIMEOUT_SECONDS = $AttemptTimeoutSeconds.ToString()
        $env:INCIDENTCOMPASS_EVALUATION_ARTIFACTS_DIRECTORY = $resultDirectory
        $env:INCIDENTCOMPASS_TESTER_EVALUATION_OUTPUT = "/artifacts/$resultLeaf"
        $env:INCIDENTCOMPASS_TESTER_EVALUATION_RUNS = $Runs.ToString()
        $env:INCIDENTCOMPASS_TESTER_EVALUATION_REVISION = $revision
        $env:INCIDENTCOMPASS_TESTER_EVALUATION_CONTENT_IDENTITY = $contentIdentity.Identity
        $env:INCIDENTCOMPASS_TESTER_EVALUATION_CONTENT_DIRTY = $contentIdentity.Dirty.ToString().ToLowerInvariant()

        Invoke-EvaluationCompose @("build", "api", "worker", "tester")
        Invoke-EvaluationCompose @("up", "-d", "--force-recreate", "postgres", "api", "worker")
        Invoke-EvaluationCompose @(
            "run",
            "--rm",
            "--no-deps",
            "tester",
            "--evaluation",
            "--attempt-timeout-seconds",
            $AttemptTimeoutSeconds.ToString())
    }
    catch {
        $evaluationError = $_
    }
    finally {
        & docker @composeArgs down --volumes | Out-Host
        if ($LASTEXITCODE -ne 0) {
            $cleanupError = "docker compose cleanup failed with exit code $LASTEXITCODE for project $composeProject."
        }
        Restore-ProcessEnvironment $saved
    }

    if ($null -ne $evaluationError) {
        if ($null -ne $cleanupError) {
            throw "$($evaluationError.Exception.Message) Cleanup also failed: $cleanupError"
        }
        throw $evaluationError
    }
    if ($null -ne $cleanupError) {
        throw $cleanupError
    }

    if (-not (Test-Path -LiteralPath $absoluteResultPath)) {
        throw "Tester did not retain the evaluation result at $absoluteResultPath."
    }

    $result = Get-Content -LiteralPath $absoluteResultPath -Raw | ConvertFrom-Json
    if ($result.schemaVersion -ne 3 -or $result.corpusVersion -ne "triage-evaluation-corpus-v1") {
        throw "Tester produced an unsupported evaluation result contract."
    }
    if ($result.evaluatedRevision -ne $revision -or
        $result.evaluatedContentIdentity -ne $contentIdentity.Identity -or
        [DateTimeOffset]$result.startedAtUtc -lt $evaluationStartedAtUtc) {
        throw "Tester result does not belong to this evaluation invocation."
    }

    Write-Output "Evaluation result retained at $absoluteResultPath"
    exit $(if ($result.passed) { 0 } else { 1 })
}
finally {
    Set-Location $previousLocation
}
