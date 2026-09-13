using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IncidentCompass.IntegrationTests;

public sealed class ProductionOperationsTests
{
    public static TheoryData<string, string?> InvalidPreflightSettings => new()
    {
        { "POSTGRES_USER", "incidentcompass" },
        { "POSTGRES_PASSWORD", "incidentcompass_dev_password" },
        { "INCIDENTCOMPASS_API_KEY_AUTH_ENABLED", "false" },
        { "INCIDENTCOMPASS_API_KEY_SHA256", null },
        { "INCIDENTCOMPASS_LLM_MODEL", "local-model" },
        { "INCIDENTCOMPASS_EMBEDDINGS_MODEL", null },
        { "INCIDENTCOMPASS_EMBEDDINGS_MODEL", "local-embedding-model" },
        { "INCIDENTCOMPASS_EMBEDDINGS_MODEL", "change-me-to-a-real-embedding-model" },
        { "INCIDENTCOMPASS_EMBEDDINGS_PROVIDER", "Mock" },
        { "INCIDENTCOMPASS_EMBEDDINGS_PROVIDER", "localonnx" },
        { "INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID", "local-oai" },
        { "INCIDENTCOMPASS_SOURCE_ROOT", "relative/source" },
        { "INCIDENTCOMPASS_GITHUB_TOKEN", null },
        { "INCIDENTCOMPASS_TELEGRAM_ENABLED", "true" }
    };

    /// <summary>
    /// Invalid only once the embedding provider is OpenAICompatible: with the LocalOnnx default none of
    /// these values is read, so each of them is checked on a fixture that switched provider.
    /// </summary>
    public static TheoryData<string, string?> InvalidOpenAiCompatibleEmbeddingSettings => new()
    {
        { "INCIDENTCOMPASS_EMBEDDINGS_BASE_URL", null },
        { "INCIDENTCOMPASS_EMBEDDINGS_BASE_URL", "http://provider.test" },
        { "INCIDENTCOMPASS_EMBEDDINGS_PATH", null },
        { "INCIDENTCOMPASS_EMBEDDINGS_PATH", "v1/embeddings" },
        { "INCIDENTCOMPASS_EMBEDDINGS_API_KEY", null },
        { "INCIDENTCOMPASS_EMBEDDINGS_API_KEY", "local-dev-key" },
        { "INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID", null },
        { "INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID", "local-embed" },
        { "INCIDENTCOMPASS_EMBEDDINGS_MODEL", "local-embedding-model" }
    };

    [Fact]
    public async Task ProductionPreflight_ValidConfigurationPassesWithoutPrintingSecrets()
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        Assert.False(fixture.Settings.ContainsKey("INCIDENTCOMPASS_EMBEDDINGS_PROVIDER"));
        Assert.False(fixture.Settings.ContainsKey("INCIDENTCOMPASS_EMBEDDINGS_BASE_URL"));
        Assert.False(fixture.Settings.ContainsKey("INCIDENTCOMPASS_EMBEDDINGS_API_KEY"));

        var result = await RunPowerShellAsync(
            "-File", fixture.Script("production-preflight.ps1"),
            "-EnvironmentFile", fixture.EnvironmentFile);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Production preflight passed", result.StandardOutput, StringComparison.Ordinal);
        fixture.AssertSecretsAbsent(result);
    }

    [Theory]
    [MemberData(nameof(InvalidPreflightSettings))]
    public async Task ProductionPreflight_RejectsUnsafeOrMissingBindings(string name, string? value)
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        fixture.Set(name, value);
        await fixture.WriteEnvironmentAsync();

        var result = await RunPowerShellAsync(
            "-File", fixture.Script("production-preflight.ps1"),
            "-EnvironmentFile", fixture.EnvironmentFile);

        Assert.NotEqual(0, result.ExitCode);
        fixture.AssertSecretsAbsent(result);
    }

    [Fact]
    public async Task ProductionPreflight_OpenAiCompatibleEmbeddingsPassWithTheirEndpointAndKey()
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        fixture.UseOpenAiCompatibleEmbeddings();
        await fixture.WriteEnvironmentAsync();

        var result = await RunPowerShellAsync(
            "-File", fixture.Script("production-preflight.ps1"),
            "-EnvironmentFile", fixture.EnvironmentFile);

        Assert.Equal(0, result.ExitCode);
        fixture.AssertSecretsAbsent(result);
    }

    [Theory]
    [MemberData(nameof(InvalidOpenAiCompatibleEmbeddingSettings))]
    public async Task ProductionPreflight_OpenAiCompatibleEmbeddingsRejectMissingOrUnsafeBindings(string name, string? value)
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        fixture.UseOpenAiCompatibleEmbeddings();
        fixture.Set(name, value);
        await fixture.WriteEnvironmentAsync();

        var result = await RunPowerShellAsync(
            "-File", fixture.Script("production-preflight.ps1"),
            "-EnvironmentFile", fixture.EnvironmentFile);

        Assert.NotEqual(0, result.ExitCode);
        fixture.AssertSecretsAbsent(result);
    }

    [Fact]
    public async Task ProductionPreflight_RejectsAmbientOptionalBindingOverride()
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        fixture.Set("INCIDENTCOMPASS_TELEGRAM_ENABLED", null);
        await fixture.WriteEnvironmentAsync();
        const string ambientToken = "123456:ambient-telegram-secret-value";

        var result = await RunProcessAsync(
            "pwsh",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INCIDENTCOMPASS_TELEGRAM_ENABLED"] = "true",
                ["INCIDENTCOMPASS_TELEGRAM_ROUTE_ID"] = "ambient-route",
                ["INCIDENTCOMPASS_TELEGRAM_CHAT_ID"] = "12345",
                ["INCIDENTCOMPASS_TELEGRAM_BOT_TOKEN"] = ambientToken
            },
            "-NoProfile", "-File", fixture.Script("production-preflight.ps1"),
            "-EnvironmentFile", fixture.EnvironmentFile);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Ambient process environment overrides", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(ambientToken, result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionCompose_ActivatesOnlyRuntimeServicesAndUsesLoopbackBoundedDefaults()
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        var composeArguments = fixture.ComposeArguments("config", "--format", "json");

        var rendered = await RunProcessAsync("docker", fixture.Settings, composeArguments);

        Assert.Equal(0, rendered.ExitCode);
        using var document = JsonDocument.Parse(rendered.StandardOutput);
        var services = document.RootElement.GetProperty("services");
        Assert.Equal("127.0.0.1", services.GetProperty("api").GetProperty("ports")[0].GetProperty("host_ip").GetString());
        Assert.Equal("127.0.0.1", services.GetProperty("postgres").GetProperty("ports")[0].GetProperty("host_ip").GetString());
        Assert.Equal("unless-stopped", services.GetProperty("api").GetProperty("restart").GetString());
        Assert.Equal("unless-stopped", services.GetProperty("worker").GetProperty("restart").GetString());
        Assert.Equal("json-file", services.GetProperty("postgres").GetProperty("logging").GetProperty("driver").GetString());
        Assert.False(services.TryGetProperty("postgres-restore", out _));
        AssertLocalEmbeddingModelDefaults(document.RootElement, fixture.Settings["INCIDENTCOMPASS_COMPOSE_PROJECT"]);
        Assert.False(fixture.Settings.ContainsKey("INCIDENTCOMPASS_MEMORY_SEED_OWNER"));
        AssertMemorySeedScopeShared(services, fixture.Settings["INCIDENTCOMPASS_TENANT_ID"], "production");

        var recoveryRendered = await RunProcessAsync(
            "docker", fixture.Settings, fixture.ComposeArguments("--profile", "recovery", "config", "--format", "json"));
        Assert.Equal(0, recoveryRendered.ExitCode);
        using var recoveryDocument = JsonDocument.Parse(recoveryRendered.StandardOutput);
        Assert.Equal("recovery", recoveryDocument.RootElement.GetProperty("services")
            .GetProperty("postgres-restore").GetProperty("profiles")[0].GetString());

        var active = await RunProcessAsync("docker", fixture.Settings, fixture.ComposeArguments("config", "--services"));
        Assert.Equal(0, active.ExitCode);
        Assert.Equal(["api", "postgres", "worker"], active.StandardOutput.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray());

        var apiDockerfile = await File.ReadAllTextAsync(Path.Combine(fixture.RepositoryRoot, "src", "IncidentCompass.Api", "Dockerfile"));
        var workerDockerfile = await File.ReadAllTextAsync(Path.Combine(fixture.RepositoryRoot, "src", "IncidentCompass.Worker", "Dockerfile"));
        Assert.Contains("USER incidentcompass", apiDockerfile, StringComparison.Ordinal);
        Assert.Contains("USER incidentcompass", workerDockerfile, StringComparison.Ordinal);
        var modelDirectoryCreated = workerDockerfile.IndexOf("mkdir -p /app/models", StringComparison.Ordinal);
        Assert.InRange(modelDirectoryCreated, 0, workerDockerfile.IndexOf("USER incidentcompass", StringComparison.Ordinal));

        var gitIgnore = await File.ReadAllLinesAsync(Path.Combine(fixture.RepositoryRoot, ".gitignore"));
        var dockerIgnore = await File.ReadAllLinesAsync(Path.Combine(fixture.RepositoryRoot, ".dockerignore"));
        Assert.Contains(".env.production", gitIgnore);
        Assert.Contains(".env.production", dockerIgnore);
    }

    [Fact]
    public async Task ProductionCompose_ApiAndWorkerTakeTheConfiguredMemorySeedOwner()
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        fixture.Set("INCIDENTCOMPASS_MEMORY_SEED_OWNER", "operator-corpus");
        await fixture.WriteEnvironmentAsync();

        var rendered = await RunProcessAsync("docker", fixture.Settings, fixture.ComposeArguments("config", "--format", "json"));

        Assert.Equal(0, rendered.ExitCode);
        using var document = JsonDocument.Parse(rendered.StandardOutput);
        AssertMemorySeedScopeShared(
            document.RootElement.GetProperty("services"),
            fixture.Settings["INCIDENTCOMPASS_TENANT_ID"],
            "operator-corpus");
    }

    [Fact]
    public async Task BackupRotation_RemovesOnlyOldVerifiedOwnedPairs()
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        var backupDirectory = Directory.CreateDirectory(Path.Combine(fixture.TempDirectory, "backups")).FullName;
        var owned = new List<(string Dump, string Manifest)>();
        for (var index = 0; index < 9; index++)
        {
            var timestamp = DateTimeOffset.Parse("2026-09-03T00:00:00Z", CultureInfo.InvariantCulture).AddMinutes(index);
            var baseName = $"incidentcompass-backup-{timestamp:yyyyMMdd'T'HHmmssfff'Z'}-{index:x8}";
            var dump = Path.Combine(backupDirectory, baseName + ".dump");
            var manifest = Path.Combine(backupDirectory, baseName + ".manifest.json");
            await File.WriteAllBytesAsync(dump, Encoding.UTF8.GetBytes("dump-" + index));
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
            {
                kind = "IncidentCompass.PostgresBackup",
                formatVersion = 1,
                createdAtUtc = timestamp,
                dumpFile = Path.GetFileName(dump),
                sizeBytes = new FileInfo(dump).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dump))).ToLowerInvariant(),
                postgresFormat = "custom"
            }));
            owned.Add((dump, manifest));
        }

        var unrelated = Path.Combine(backupDirectory, "operator-notes.txt");
        var orphan = Path.Combine(backupDirectory, "incidentcompass-backup-20260903T010000000Z-deadbeef.dump");
        var partial = Path.Combine(backupDirectory, "incidentcompass-backup-20260903T010000000Z-feedbeef.partial");
        await File.WriteAllTextAsync(unrelated, "keep");
        await File.WriteAllTextAsync(orphan, "keep");
        await File.WriteAllTextAsync(partial, "keep");

        var common = fixture.Script("production-common.ps1").Replace("'", "''", StringComparison.Ordinal);
        var directory = backupDirectory.Replace("'", "''", StringComparison.Ordinal);
        var result = await RunPowerShellAsync("-Command", $". '{common}'; Remove-ExpiredOwnedBackupPairs -Directory '{directory}' -Retain 7");

        Assert.Equal(0, result.ExitCode);
        Assert.All(owned.Take(2), pair =>
        {
            Assert.False(File.Exists(pair.Dump));
            Assert.False(File.Exists(pair.Manifest));
        });
        Assert.All(owned.Skip(2), pair =>
        {
            Assert.True(File.Exists(pair.Dump));
            Assert.True(File.Exists(pair.Manifest));
        });
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(orphan));
        Assert.True(File.Exists(partial));

        var crossNameDirectory = Directory.CreateDirectory(Path.Combine(fixture.TempDirectory, "cross-name")).FullName;
        var legitimateDump = Path.Combine(crossNameDirectory,
            "incidentcompass-backup-20260904T010000000Z-aaaaaaaa.dump");
        var legitimateManifest = legitimateDump[..^5] + ".manifest.json";
        var mismatchedManifest = Path.Combine(crossNameDirectory,
            "incidentcompass-backup-20260903T010000000Z-bbbbbbbb.manifest.json");
        await File.WriteAllTextAsync(legitimateDump, "legitimate-dump");
        var legitimateHash = Convert.ToHexString(
            SHA256.HashData(await File.ReadAllBytesAsync(legitimateDump))).ToLowerInvariant();
        await File.WriteAllTextAsync(legitimateManifest, JsonSerializer.Serialize(new
        {
            kind = "IncidentCompass.PostgresBackup",
            formatVersion = 1,
            createdAtUtc = DateTimeOffset.Parse("2026-09-04T01:00:00Z", CultureInfo.InvariantCulture),
            dumpFile = Path.GetFileName(legitimateDump),
            sizeBytes = new FileInfo(legitimateDump).Length,
            sha256 = legitimateHash,
            postgresFormat = "custom"
        }));
        await File.WriteAllTextAsync(mismatchedManifest, JsonSerializer.Serialize(new
        {
            kind = "IncidentCompass.PostgresBackup",
            formatVersion = 1,
            createdAtUtc = DateTimeOffset.Parse("2026-09-03T01:00:00Z", CultureInfo.InvariantCulture),
            dumpFile = Path.GetFileName(legitimateDump),
            sizeBytes = new FileInfo(legitimateDump).Length,
            sha256 = legitimateHash,
            postgresFormat = "custom"
        }));

        var crossName = crossNameDirectory.Replace("'", "''", StringComparison.Ordinal);
        var crossNameResult = await RunPowerShellAsync(
            "-Command", $". '{common}'; Remove-ExpiredOwnedBackupPairs -Directory '{crossName}' -Retain 1");

        Assert.Equal(0, crossNameResult.ExitCode);
        Assert.True(File.Exists(legitimateDump));
        Assert.True(File.Exists(legitimateManifest));
        Assert.True(File.Exists(mismatchedManifest));
    }

    [Fact]
    public async Task Restore_RejectsHashMismatchAndExactConfirmationFailureWithoutPrintingSecrets()
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        var backup = await fixture.CreateBackupPairAsync();
        var tampered = await File.ReadAllBytesAsync(backup.Dump);
        tampered[0] ^= 0xff;
        await File.WriteAllBytesAsync(backup.Dump, tampered);

        var hashFailure = await RunPowerShellAsync(
            "-File", fixture.Script("postgres-restore.ps1"), "-EnvironmentFile", fixture.EnvironmentFile,
            "-BackupFile", backup.Dump, "-TargetProject", "recovery-safe", "-ConfirmTarget", "recovery-safe", "-WhatIf");
        Assert.NotEqual(0, hashFailure.ExitCode);
        Assert.Contains("SHA-256", hashFailure.StandardError, StringComparison.Ordinal);
        fixture.AssertSecretsAbsent(hashFailure);

        var confirmationFailure = await RunPowerShellAsync(
            "-File", fixture.Script("postgres-restore.ps1"), "-EnvironmentFile", fixture.EnvironmentFile,
            "-BackupFile", backup.Dump, "-TargetProject", "recovery-safe", "-ConfirmTarget", "RECOVERY-SAFE", "-WhatIf");
        Assert.NotEqual(0, confirmationFailure.ExitCode);
        Assert.Contains("exactly match", confirmationFailure.StandardError, StringComparison.Ordinal);
        fixture.AssertSecretsAbsent(confirmationFailure);
    }

    [Fact]
    public async Task BoundedCommand_StopsAtTimeoutWithoutEchoingArguments()
    {
        await using var fixture = await ProductionFixture.CreateAsync();
        var common = fixture.Script("production-common.ps1").Replace("'", "''", StringComparison.Ordinal);
        const string secretArgument = "bounded-secret-must-not-appear";
        var command = $". '{common}'; try {{ Invoke-BoundedProductionCommand -FilePath 'pwsh' " +
            $"-ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 5 # {secretArgument}') -TimeoutSeconds 1 }} " +
            "catch { Write-Error $_.Exception.Message; exit 1 }";

        var result = await RunPowerShellAsync("-Command", command);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("bounded timeout", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secretArgument, result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Worker writes the memory corpus and its synchronization status under its seed tenant and
    /// owner, and the API's memory health readers look them up under the API's, so both services must
    /// render the same non-empty scope, and it must be the one the environment file configures.
    /// </summary>
    private static void AssertMemorySeedScopeShared(JsonElement services, string expectedTenant, string expectedOwner)
    {
        var api = services.GetProperty("api").GetProperty("environment");
        var worker = services.GetProperty("worker").GetProperty("environment");
        foreach (var (name, expected) in new[]
                 {
                     ("IncidentCompass__Memory__Seed__TenantId", expectedTenant),
                     ("IncidentCompass__Memory__Seed__Owner", expectedOwner)
                 })
        {
            var workerValue = worker.GetProperty(name).GetString();
            Assert.False(string.IsNullOrWhiteSpace(workerValue), $"The worker renders no {name}.");
            Assert.Equal(expected, workerValue);
            Assert.Equal(workerValue, api.GetProperty(name).GetString());
        }
    }

    /// <summary>
    /// The rendered production stack with no embedding provider set: the Worker embeds with the local
    /// model from the named volume at <c>/app/models</c>, the route provider id is the local entry, the
    /// OpenAI-compatible embedding values render empty rather than being required, and the Api mounts
    /// no model volume.
    /// </summary>
    private static void AssertLocalEmbeddingModelDefaults(JsonElement root, string projectName)
    {
        var services = root.GetProperty("services");
        var workerEnvironment = services.GetProperty("worker").GetProperty("environment");
        Assert.Equal("LocalOnnx", workerEnvironment.GetProperty("IncidentCompass__Embeddings__Provider").GetString());
        Assert.Equal("/app/models", workerEnvironment.GetProperty("IncidentCompass__Embeddings__LocalOnnx__ModelDirectory").GetString());
        Assert.Equal("local-embed", workerEnvironment.GetProperty("INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID").GetString());
        Assert.Equal("intfloat/multilingual-e5-small", workerEnvironment.GetProperty("INCIDENTCOMPASS_EMBEDDINGS_MODEL").GetString());
        Assert.Equal(string.Empty, workerEnvironment.GetProperty("IncidentCompass__Embeddings__OpenAiCompatible__BaseUrl").GetString());
        Assert.Equal(string.Empty, workerEnvironment.GetProperty("IncidentCompass__Embeddings__OpenAiCompatible__ApiKey").GetString());

        var modelMount = Assert.Single(
            services.GetProperty("worker").GetProperty("volumes").EnumerateArray(),
            mount => mount.GetProperty("target").GetString() == "/app/models");
        Assert.Equal("volume", modelMount.GetProperty("type").GetString());
        Assert.Equal("embedding-models", modelMount.GetProperty("source").GetString());
        Assert.False(modelMount.TryGetProperty("read_only", out var readOnly) && readOnly.GetBoolean());

        var api = services.GetProperty("api");
        Assert.True(
            !api.TryGetProperty("volumes", out var apiVolumes) ||
            apiVolumes.EnumerateArray().All(mount => mount.GetProperty("target").GetString() != "/app/models"));

        Assert.Equal(
            projectName + "_embedding-models",
            root.GetProperty("volumes").GetProperty("embedding-models").GetProperty("name").GetString());
    }

    private static Task<ProcessResult> RunPowerShellAsync(params string[] arguments) =>
        RunProcessAsync("pwsh", null, ["-NoProfile", .. arguments]);

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyDictionary<string, string>? environment,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = FindRepositoryRoot()
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new FileInfo(sourceFilePath).Directory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IncidentCompass.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Could not find repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class ProductionFixture : IAsyncDisposable
    {
        private ProductionFixture(string repositoryRoot, string tempDirectory, Dictionary<string, string> settings)
        {
            RepositoryRoot = repositoryRoot;
            TempDirectory = tempDirectory;
            Settings = settings;
            EnvironmentFile = Path.Combine(tempDirectory, ".env.production");
        }

        public string RepositoryRoot { get; }
        public string TempDirectory { get; }
        public string EnvironmentFile { get; }
        public Dictionary<string, string> Settings { get; }

        public static async Task<ProductionFixture> CreateAsync()
        {
            var root = FindRepositoryRoot();
            var temp = Directory.CreateTempSubdirectory("incidentcompass-production-").FullName;
            var source = Directory.CreateDirectory(Path.Combine(temp, "source")).FullName;
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INCIDENTCOMPASS_COMPOSE_PROJECT"] = "incidentcompass-production-test",
                ["POSTGRES_DB"] = "incidentcompass_prod_test",
                ["POSTGRES_USER"] = "incidentcompass_operator_test",
                ["POSTGRES_PASSWORD"] = "database-secret-value-12345",
                ["INCIDENTCOMPASS_API_KEY_AUTH_ENABLED"] = "true",
                ["INCIDENTCOMPASS_API_KEY_ID"] = "primary-operator",
                ["INCIDENTCOMPASS_TENANT_ID"] = "production-test",
                ["INCIDENTCOMPASS_API_KEY_SHA256"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["INCIDENTCOMPASS_LLM_BASE_URL"] = "https://provider.test",
                ["INCIDENTCOMPASS_LLM_CHAT_COMPLETIONS_PATH"] = "/v1/chat/completions",
                ["INCIDENTCOMPASS_LLM_MODEL"] = "production-chat-model",
                ["INCIDENTCOMPASS_LLM_API_KEY"] = "model-secret-value-12345",
                // No embedding provider, endpoint or key: the LocalOnnx default reads none of them.
                ["INCIDENTCOMPASS_EMBEDDINGS_MODEL"] = "intfloat/multilingual-e5-small",
                ["INCIDENTCOMPASS_ALLOW_INSECURE_LOOPBACK_PROVIDER"] = "false",
                ["INCIDENTCOMPASS_SOURCE_ROOT"] = source,
                ["INCIDENTCOMPASS_SOURCE_SERVICE"] = "orders",
                ["INCIDENTCOMPASS_SOURCE_RELEASE"] = "v1.0.0",
                ["INCIDENTCOMPASS_GITHUB_OWNER"] = "owner",
                ["INCIDENTCOMPASS_GITHUB_REPOSITORY"] = "repository",
                ["INCIDENTCOMPASS_GITHUB_TOKEN"] = "github-secret-value-12345",
                ["INCIDENTCOMPASS_MEMORY_SEED_ENABLED"] = "false",
                ["INCIDENTCOMPASS_TELEGRAM_ENABLED"] = "false"
            };
            var fixture = new ProductionFixture(root, temp, settings);
            await fixture.WriteEnvironmentAsync();
            return fixture;
        }

        public string Script(string name) => Path.Combine(RepositoryRoot, "scripts", name);

        public void UseOpenAiCompatibleEmbeddings()
        {
            Settings["INCIDENTCOMPASS_EMBEDDINGS_PROVIDER"] = "OpenAICompatible";
            Settings["INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID"] = "local-oai";
            Settings["INCIDENTCOMPASS_EMBEDDINGS_BASE_URL"] = "https://provider.test";
            Settings["INCIDENTCOMPASS_EMBEDDINGS_PATH"] = "/v1/embeddings";
            Settings["INCIDENTCOMPASS_EMBEDDINGS_MODEL"] = "production-embedding-model";
            Settings["INCIDENTCOMPASS_EMBEDDINGS_API_KEY"] = "embedding-secret-value-12345";
        }

        public string[] ComposeArguments(params string[] tail) =>
        [
            "compose", "-f", Path.Combine(RepositoryRoot, "docker-compose.yml"),
            "-f", Path.Combine(RepositoryRoot, "compose.production.yml"),
            "--env-file", EnvironmentFile, "--project-name", Settings["INCIDENTCOMPASS_COMPOSE_PROJECT"],
            .. tail
        ];

        public void Set(string name, string? value)
        {
            if (value is null)
            {
                Settings.Remove(name);
            }
            else
            {
                Settings[name] = value;
            }
        }

        public Task WriteEnvironmentAsync() => File.WriteAllLinesAsync(
            EnvironmentFile,
            Settings.Select(entry => $"{entry.Key}={entry.Value}"));

        public async Task<(string Dump, string Manifest)> CreateBackupPairAsync()
        {
            var dump = Path.Combine(TempDirectory, "incidentcompass-backup-20260903T010000000Z-deadbeef.dump");
            await File.WriteAllTextAsync(dump, "valid-custom-dump-fixture");
            var manifest = dump[..^5] + ".manifest.json";
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new
            {
                kind = "IncidentCompass.PostgresBackup",
                formatVersion = 1,
                createdAtUtc = DateTimeOffset.Parse("2026-09-03T01:00:00Z", CultureInfo.InvariantCulture),
                dumpFile = Path.GetFileName(dump),
                sizeBytes = new FileInfo(dump).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dump))).ToLowerInvariant(),
                postgresFormat = "custom"
            }));
            return (dump, manifest);
        }

        public void AssertSecretsAbsent(ProcessResult result)
        {
            var combined = result.StandardOutput + result.StandardError;
            foreach (var name in new[]
                     {
                         "POSTGRES_PASSWORD", "INCIDENTCOMPASS_LLM_API_KEY",
                         "INCIDENTCOMPASS_EMBEDDINGS_API_KEY", "INCIDENTCOMPASS_GITHUB_TOKEN"
                     })
            {
                if (Settings.TryGetValue(name, out var value))
                {
                    Assert.DoesNotContain(value, combined, StringComparison.Ordinal);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(TempDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
