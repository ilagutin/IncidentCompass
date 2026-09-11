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
        "INCIDENTCOMPASS_EMBEDDINGS_BASE_URL", "INCIDENTCOMPASS_EMBEDDINGS_PATH",
        "INCIDENTCOMPASS_EMBEDDINGS_MODEL", "INCIDENTCOMPASS_EMBEDDINGS_API_KEY",
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

    $realBindings = @{
        INCIDENTCOMPASS_LLM_MODEL = @("local-model", "mock-chat")
        INCIDENTCOMPASS_LLM_API_KEY = @("local-dev-key")
        INCIDENTCOMPASS_EMBEDDINGS_MODEL = @("local-embedding-model", "mock-embedding", "mock-memory-embedding-v1")
        INCIDENTCOMPASS_EMBEDDINGS_API_KEY = @("local-dev-key")
        INCIDENTCOMPASS_GITHUB_OWNER = @()
        INCIDENTCOMPASS_GITHUB_REPOSITORY = @()
        INCIDENTCOMPASS_GITHUB_TOKEN = @()
        INCIDENTCOMPASS_SOURCE_SERVICE = @()
        INCIDENTCOMPASS_SOURCE_RELEASE = @()
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
    foreach ($urlName in @("INCIDENTCOMPASS_LLM_BASE_URL", "INCIDENTCOMPASS_EMBEDDINGS_BASE_URL")) {
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

    foreach ($pathName in @("INCIDENTCOMPASS_LLM_CHAT_COMPLETIONS_PATH", "INCIDENTCOMPASS_EMBEDDINGS_PATH")) {
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
