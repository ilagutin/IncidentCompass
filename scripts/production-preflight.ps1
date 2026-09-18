param(
    [string] $EnvironmentFile = ".env.production"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "production-common.ps1")

try {
    $environmentPath = Resolve-ProductionEnvironmentFile -Path $EnvironmentFile
    $values = Read-ProductionEnvironment -Path $environmentPath
    Assert-NoAmbientProductionOverrides -Values $values

    $required = @(
        "INCIDENTCOMPASS_COMPOSE_PROJECT",
        "POSTGRES_DB", "POSTGRES_USER", "POSTGRES_PASSWORD",
        "INCIDENTCOMPASS_API_KEY_AUTH_ENABLED", "INCIDENTCOMPASS_API_KEY_ID",
        "INCIDENTCOMPASS_TENANT_ID", "INCIDENTCOMPASS_API_KEY_SHA256",
        "INCIDENTCOMPASS_LLM_BASE_URL", "INCIDENTCOMPASS_LLM_CHAT_COMPLETIONS_PATH",
        "INCIDENTCOMPASS_LLM_MODEL", "INCIDENTCOMPASS_LLM_API_KEY",
        "INCIDENTCOMPASS_EMBEDDINGS_MODEL",
        "INCIDENTCOMPASS_SOURCE_ROOT", "INCIDENTCOMPASS_SOURCE_SERVICE",
        "INCIDENTCOMPASS_SOURCE_RELEASE", "INCIDENTCOMPASS_GITHUB_OWNER",
        "INCIDENTCOMPASS_GITHUB_REPOSITORY", "INCIDENTCOMPASS_GITHUB_TOKEN"
    )
    foreach ($name in $required) {
        $null = Get-RequiredProductionValue -Values $values -Name $name
    }

    $projectName = Get-RequiredProductionValue $values "INCIDENTCOMPASS_COMPOSE_PROJECT"
    if ($projectName -notmatch '^[a-z0-9][a-z0-9_-]{2,62}$') {
        throw "Production Compose project name is invalid."
    }

    foreach ($name in @("POSTGRES_DB", "POSTGRES_USER")) {
        $value = Get-RequiredProductionValue $values $name
        if ($value -notmatch '^[A-Za-z_][A-Za-z0-9_-]{0,62}$') {
            throw "Production setting '$name' has an unsafe database identifier."
        }
        Assert-ProductionValueIsConfigured $name $value @("incidentcompass")
    }
    $databasePassword = Get-RequiredProductionValue $values "POSTGRES_PASSWORD"
    Assert-ProductionValueIsConfigured "POSTGRES_PASSWORD" $databasePassword @("incidentcompass_dev_password")
    if ($databasePassword.Length -lt 16) {
        throw "Production setting 'POSTGRES_PASSWORD' must contain at least 16 characters."
    }

    $authEnabled = Get-RequiredProductionValue $values "INCIDENTCOMPASS_API_KEY_AUTH_ENABLED"
    if (-not (Test-ProductionBoolean $authEnabled) -or $authEnabled -ne "true") {
        throw "Production API-key authentication must be enabled."
    }
    $apiDigest = Get-RequiredProductionValue $values "INCIDENTCOMPASS_API_KEY_SHA256"
    Assert-ProductionValueIsConfigured "INCIDENTCOMPASS_API_KEY_SHA256" $apiDigest
    if ($apiDigest -notmatch '^[0-9A-Fa-f]{64}$' -or $apiDigest -match '^(.)\1{63}$') {
        throw "Production API-key credential digest must be a non-placeholder SHA-256 hex value."
    }
    $identityLimits = @{
        INCIDENTCOMPASS_API_KEY_ID = 64
        INCIDENTCOMPASS_TENANT_ID = 128
    }
    foreach ($entry in $identityLimits.GetEnumerator()) {
        $name = $entry.Key
        $value = Get-RequiredProductionValue $values $name
        Assert-ProductionValueIsConfigured $name $value
        if ($value.Length -gt $entry.Value -or $value -match '[^!-~]') {
            throw "Production API-key identity setting '$name' is invalid."
        }
    }

    # The embedding provider decides which embedding settings are read. LocalOnnx, the default in both
    # compose files, embeds on the Worker and reads no endpoint, path or key. OpenAICompatible reads
    # all three, and Compose cannot require them conditionally, so they are required here. The route
    # provider id must name the shipped provider entry of the same kind, or the Worker's adapter
    # refuses the route.
    $embeddingProvider = if ($values.ContainsKey("INCIDENTCOMPASS_EMBEDDINGS_PROVIDER") -and
        -not [string]::IsNullOrWhiteSpace([string] $values["INCIDENTCOMPASS_EMBEDDINGS_PROVIDER"])) {
        [string] $values["INCIDENTCOMPASS_EMBEDDINGS_PROVIDER"]
    } else { "LocalOnnx" }
    if ($embeddingProvider -cnotin @("LocalOnnx", "OpenAICompatible")) {
        throw "INCIDENTCOMPASS_EMBEDDINGS_PROVIDER must be LocalOnnx or OpenAICompatible."
    }
    $embeddingProviderIds = @{ LocalOnnx = "local-embed"; OpenAICompatible = "local-oai" }
    $embeddingProviderId = if ($values.ContainsKey("INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID") -and
        -not [string]::IsNullOrWhiteSpace([string] $values["INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID"])) {
        [string] $values["INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID"]
    } else { "local-embed" }
    $expectedEmbeddingProviderId = $embeddingProviderIds[$embeddingProvider]
    if ($embeddingProviderId -cne $expectedEmbeddingProviderId) {
        throw "INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID must be '$expectedEmbeddingProviderId' when the embedding provider is $embeddingProvider."
    }
    $openAiCompatibleEmbeddings = $embeddingProvider -ceq "OpenAICompatible"
    if ($openAiCompatibleEmbeddings) {
        foreach ($name in @("INCIDENTCOMPASS_EMBEDDINGS_BASE_URL", "INCIDENTCOMPASS_EMBEDDINGS_PATH", "INCIDENTCOMPASS_EMBEDDINGS_API_KEY")) {
            $null = Get-RequiredProductionValue -Values $values -Name $name
        }
    }

    # The relevance judge provider is pinned to LocalOnnx in compose.production.yml and is not read
    # from this file. An entry that names any other provider is refused rather than ignored, so an
    # operator who believes they chose the mock judge, a deterministic stand-in that is not a
    # governance boundary, learns here that production never runs it.
    foreach ($key in @($values.Keys)) {
        if ($key -match '(?i)relevance_?judge(_|__)provider' -and [string] $values[$key] -cne "LocalOnnx") {
            throw "Production setting '$key' names a relevance judge other than LocalOnnx; the provider is pinned to LocalOnnx, and the Mock judge must never run on a production host."
        }
    }

    $realBindings = @{
        INCIDENTCOMPASS_LLM_MODEL = @("local-model", "mock-chat")
        INCIDENTCOMPASS_LLM_API_KEY = @("local-dev-key")
        INCIDENTCOMPASS_EMBEDDINGS_MODEL = @("local-embedding-model", "mock-embedding", "mock-memory-embedding-v1")
        INCIDENTCOMPASS_GITHUB_OWNER = @()
        INCIDENTCOMPASS_GITHUB_REPOSITORY = @()
        INCIDENTCOMPASS_GITHUB_TOKEN = @()
        INCIDENTCOMPASS_SOURCE_SERVICE = @()
        INCIDENTCOMPASS_SOURCE_RELEASE = @()
    }
    if ($openAiCompatibleEmbeddings) {
        $realBindings["INCIDENTCOMPASS_EMBEDDINGS_API_KEY"] = @("local-dev-key")
    }
    foreach ($entry in $realBindings.GetEnumerator()) {
        Assert-ProductionValueIsConfigured $entry.Key ([string] $values[$entry.Key]) $entry.Value
    }

    $allowInsecure = if ($values.ContainsKey("INCIDENTCOMPASS_ALLOW_INSECURE_LOOPBACK_PROVIDER")) {
        [string] $values["INCIDENTCOMPASS_ALLOW_INSECURE_LOOPBACK_PROVIDER"]
    } else { "false" }
    if (-not (Test-ProductionBoolean $allowInsecure)) {
        throw "INCIDENTCOMPASS_ALLOW_INSECURE_LOOPBACK_PROVIDER must be true or false."
    }
    $providerUrlNames = @("INCIDENTCOMPASS_LLM_BASE_URL")
    $providerPathNames = @("INCIDENTCOMPASS_LLM_CHAT_COMPLETIONS_PATH")
    if ($openAiCompatibleEmbeddings) {
        $providerUrlNames += "INCIDENTCOMPASS_EMBEDDINGS_BASE_URL"
        $providerPathNames += "INCIDENTCOMPASS_EMBEDDINGS_PATH"
    }
    foreach ($urlName in $providerUrlNames) {
        $url = Get-RequiredProductionValue $values $urlName
        $uri = $null
        if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref] $uri)) {
            throw "Production setting '$urlName' must be an absolute provider URL."
        }
        $localHosts = @("localhost", "127.0.0.1", "::1", "host.docker.internal")
        if ($uri.Scheme -ne "https" -and
            -not ($uri.Scheme -eq "http" -and $allowInsecure -eq "true" -and $uri.Host -in $localHosts)) {
            throw "Production provider URLs require HTTPS unless the explicit trusted-host loopback override is enabled."
        }
        Assert-ProductionValueIsConfigured $urlName $url
    }

    foreach ($pathName in $providerPathNames) {
        if ([string] $values[$pathName] -notmatch '^/[^/].*') {
            throw "Production provider endpoint path '$pathName' is invalid."
        }
    }

    $sourceRoot = Get-RequiredProductionValue $values "INCIDENTCOMPASS_SOURCE_ROOT"
    Assert-ProductionValueIsConfigured "INCIDENTCOMPASS_SOURCE_ROOT" $sourceRoot
    if (-not (Test-AbsoluteOperatorPath $sourceRoot) -or
        -not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "INCIDENTCOMPASS_SOURCE_ROOT must be an existing absolute host-owned directory."
    }
    $resolvedSourceRoot = (Resolve-Path -LiteralPath $sourceRoot).Path
    $filesystemRoot = [IO.Path]::GetPathRoot($resolvedSourceRoot)
    $trimCharacters = [char[]] @([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($resolvedSourceRoot.TrimEnd($trimCharacters) -eq $filesystemRoot.TrimEnd($trimCharacters)) {
        throw "INCIDENTCOMPASS_SOURCE_ROOT must select one repository, not a filesystem root."
    }

    # Optional by design: empty means code publication is not configured on this host and every push
    # refuses. A non-empty value is an operator string that ends up in a provider URL path, so its
    # shape is checked here as well as in the host that will refuse to start on a bad one.
    $publicationBaseBranch = if ($values.ContainsKey("INCIDENTCOMPASS_GITHUB_BASE_BRANCH")) {
        [string] $values["INCIDENTCOMPASS_GITHUB_BASE_BRANCH"]
    } else { "" }
    if (-not [string]::IsNullOrWhiteSpace($publicationBaseBranch)) {
        Assert-ProductionValueIsConfigured "INCIDENTCOMPASS_GITHUB_BASE_BRANCH" $publicationBaseBranch
        if ($publicationBaseBranch -notmatch '^[A-Za-z0-9][A-Za-z0-9._/-]{0,99}$' -or
            $publicationBaseBranch -match '\.\.|//' -or
            $publicationBaseBranch.EndsWith("/") -or $publicationBaseBranch.EndsWith(".lock") -or
            $publicationBaseBranch.StartsWith("incidentcompass/remediation/")) {
            throw "INCIDENTCOMPASS_GITHUB_BASE_BRANCH is not a branch name this adapter will use."
        }
    }

    $telegramEnabled = if ($values.ContainsKey("INCIDENTCOMPASS_TELEGRAM_ENABLED")) {
        [string] $values["INCIDENTCOMPASS_TELEGRAM_ENABLED"]
    } else { "false" }
    if (-not (Test-ProductionBoolean $telegramEnabled)) {
        throw "INCIDENTCOMPASS_TELEGRAM_ENABLED must be true or false."
    }
    if ($telegramEnabled -eq "true") {
        foreach ($name in @("INCIDENTCOMPASS_TELEGRAM_ROUTE_ID", "INCIDENTCOMPASS_TELEGRAM_CHAT_ID", "INCIDENTCOMPASS_TELEGRAM_BOT_TOKEN")) {
            $value = Get-RequiredProductionValue $values $name
            Assert-ProductionValueIsConfigured $name $value
        }
        if ([string] $values["INCIDENTCOMPASS_TELEGRAM_ROUTE_ID"] -notmatch '^[A-Za-z0-9_.-]{1,128}$' -or
            [string] $values["INCIDENTCOMPASS_TELEGRAM_CHAT_ID"] -notmatch '^-?[1-9][0-9]{0,19}$' -or
            [string] $values["INCIDENTCOMPASS_TELEGRAM_BOT_TOKEN"] -notmatch '^[0-9]{5,20}:[A-Za-z0-9_-]{20,80}$') {
            throw "Enabled Telegram binding has an invalid route id, chat id or bot token shape."
        }
    }

    Import-ProductionEnvironment -Values $values
    $compose = Get-ProductionComposeArguments -EnvironmentFile $environmentPath -ProjectName $projectName
    Invoke-ProductionDocker -ComposeArguments $compose -Arguments @("config", "--quiet")
    # The rendered stack, not this file, is what the Worker runs, so the judge provider is checked
    # there too: an edited or added compose file that turned on the Mock judge stops here.
    $rendered = (Invoke-ProductionDocker -ComposeArguments $compose -Arguments @("config", "--format", "json") -CaptureOutput) -join "`n"
    $renderedJudgeProvider = [string] ($rendered | ConvertFrom-Json).services.worker.environment.IncidentCompass__RelevanceJudge__Provider
    if ($renderedJudgeProvider -cne "LocalOnnx") {
        throw "The production worker must run the LocalOnnx relevance judge; the Mock judge must never run on a production host."
    }
    $services = @(Invoke-ProductionDocker -ComposeArguments $compose -Arguments @("config", "--services") -CaptureOutput)
    if (($services | Sort-Object) -join "," -ne "api,postgres,worker") {
        throw "Production Compose must activate only api, postgres and worker."
    }

    Write-Host "Production preflight passed for project '$projectName'."
}
catch {
    Write-Error -ErrorAction Continue $_.Exception.Message
    exit 1
}
