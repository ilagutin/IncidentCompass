using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection("Production recovery")]
public sealed class ProductionRecoveryTests
{
    [DockerAvailableFact]
    public async Task PopulatedV03Backup_RestoresThroughNormalStartupAndFailureTargetsRemainInspectable()
    {
        await using var fixture = await RecoveryFixture.CreateAsync();
        try
        {
            var preflight = await fixture.RunPowerShellAsync("production-preflight.ps1",
                "-EnvironmentFile", fixture.EnvironmentFile);
            AssertSuccess(preflight);
            fixture.AssertSecretsAbsent(preflight);

            var up = await fixture.RunComposeAsync(
                "up", "--detach", "--build", "--wait", "--wait-timeout", "300", "postgres", "api", "worker");
            AssertSuccess(up);
            await fixture.AssertRuntimeContainersAsync();

            var stopSourceWorker = await fixture.RunComposeAsync("stop", "worker");
            AssertSuccess(stopSourceWorker);

            var sourceConnection = fixture.ConnectionString(fixture.SourcePostgresPort);
            var origin = await ActionApprovalTestSupport.SeedOriginAsync(sourceConnection, "production-test");
            await SeedMemoryAndActionProjectionAsync(sourceConnection, origin);
            await ConvertMigrationLedgerToV03LegacyAsync(sourceConnection);
            var sourceCounts = await ReadCountsAsync(sourceConnection);
            var sourceRecoveryWork = await ReadRecoveryWorkAsync(sourceConnection);
            Assert.All(sourceCounts.Values, count => Assert.True(count > 0));
            Assert.All(sourceRecoveryWork.Values, count => Assert.Equal(1, count));

            var backup = await fixture.RunPowerShellAsync("postgres-backup.ps1",
                "-EnvironmentFile", fixture.EnvironmentFile,
                "-BackupDirectory", fixture.BackupDirectory,
                "-TimeoutSeconds", "180");
            AssertSuccess(backup);
            fixture.AssertSecretsAbsent(backup);
            var dump = Assert.Single(Directory.GetFiles(fixture.BackupDirectory, "incidentcompass-backup-*.dump"));
            var manifestPath = dump[..^5] + ".manifest.json";
            var manifestText = await File.ReadAllTextAsync(manifestPath);
            fixture.AssertSecretsAbsent(new ProcessResult(0, manifestText, string.Empty));
            using (var manifest = JsonDocument.Parse(manifestText))
            {
                Assert.Equal("custom", manifest.RootElement.GetProperty("postgresFormat").GetString());
                Assert.Matches("^[0-9a-f]{64}$", manifest.RootElement.GetProperty("sha256").GetString());
            }

            var whatIf = await fixture.RunPowerShellAsync("postgres-restore.ps1",
                "-EnvironmentFile", fixture.EnvironmentFile,
                "-BackupFile", dump,
                "-TargetProject", fixture.TargetProject,
                "-ConfirmTarget", fixture.TargetProject,
                "-TargetApiPort", fixture.TargetApiPort.ToString(CultureInfo.InvariantCulture),
                "-TargetPostgresPort", fixture.TargetPostgresPort.ToString(CultureInfo.InvariantCulture),
                "-WhatIf");
            AssertSuccess(whatIf);
            var absentAfterWhatIf = await RunProcessAsync(
                "docker", null, "volume", "inspect", fixture.TargetProject + "_postgres-data");
            Assert.NotEqual(0, absentAfterWhatIf.ExitCode);

            var restore = await fixture.RunPowerShellAsync("postgres-restore.ps1",
                "-EnvironmentFile", fixture.EnvironmentFile,
                "-BackupFile", dump,
                "-TargetProject", fixture.TargetProject,
                "-ConfirmTarget", fixture.TargetProject,
                "-TargetApiPort", fixture.TargetApiPort.ToString(CultureInfo.InvariantCulture),
                "-TargetPostgresPort", fixture.TargetPostgresPort.ToString(CultureInfo.InvariantCulture),
                "-TimeoutSeconds", "180",
                "-HealthTimeoutSeconds", "300");
            AssertSuccess(restore);
            fixture.AssertSecretsAbsent(restore);

            var targetConnection = fixture.ConnectionString(fixture.TargetPostgresPort);
            Assert.Equal(sourceCounts, await ReadCountsAsync(targetConnection));
            Assert.Equal(await ReadMigrationChecksumsAsync(sourceConnection), await ReadMigrationChecksumsAsync(targetConnection));
            Assert.Equal(sourceRecoveryWork, await ReadRecoveryWorkAsync(targetConnection));
            await AssertNoWorkerContainerAsync(fixture.TargetProject);
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.Equal(sourceRecoveryWork, await ReadRecoveryWorkAsync(targetConnection));
            Assert.Equal(sourceRecoveryWork, await ReadRecoveryWorkAsync(sourceConnection));

            var existingTarget = await fixture.RunPowerShellAsync("postgres-restore.ps1",
                "-EnvironmentFile", fixture.EnvironmentFile,
                "-BackupFile", dump,
                "-TargetProject", fixture.TargetProject,
                "-ConfirmTarget", fixture.TargetProject,
                "-TargetApiPort", fixture.TargetApiPort.ToString(CultureInfo.InvariantCulture),
                "-TargetPostgresPort", fixture.TargetPostgresPort.ToString(CultureInfo.InvariantCulture),
                "-WhatIf");
            Assert.NotEqual(0, existingTarget.ExitCode);
            Assert.Contains("already", existingTarget.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourceCounts, await ReadCountsAsync(sourceConnection));

            var malformed = await fixture.CreateMalformedBackupPairAsync();
            var malformedRestore = await fixture.RunPowerShellAsync("postgres-restore.ps1",
                "-EnvironmentFile", fixture.EnvironmentFile,
                "-BackupFile", malformed,
                "-TargetProject", fixture.FailedTargetProject,
                "-ConfirmTarget", fixture.FailedTargetProject,
                "-TargetApiPort", fixture.FailedTargetApiPort.ToString(CultureInfo.InvariantCulture),
                "-TargetPostgresPort", fixture.FailedTargetPostgresPort.ToString(CultureInfo.InvariantCulture),
                "-TimeoutSeconds", "30",
                "-HealthTimeoutSeconds", "120");
            Assert.NotEqual(0, malformedRestore.ExitCode);
            fixture.AssertSecretsAbsent(malformedRestore);

            var failedVolume = await RunProcessAsync("docker", null, "volume", "inspect",
                fixture.FailedTargetProject + "_postgres-data");
            AssertSuccess(failedVolume);
            Assert.Equal(sourceCounts, await ReadCountsAsync(sourceConnection));

            await fixture.InstallSlowPgDumpAsync();
            var timedOutBackup = await fixture.RunPowerShellAsync("postgres-backup.ps1",
                "-EnvironmentFile", fixture.EnvironmentFile,
                "-BackupDirectory", fixture.TimeoutBackupDirectory,
                "-TimeoutSeconds", "1");
            Assert.NotEqual(0, timedOutBackup.ExitCode);
            Assert.Contains("bounded timeout", timedOutBackup.StandardError, StringComparison.OrdinalIgnoreCase);
            fixture.AssertSecretsAbsent(timedOutBackup);
            Assert.Empty(Directory.GetFiles(fixture.TimeoutBackupDirectory));
            Assert.Equal(sourceCounts, await ReadCountsAsync(sourceConnection));
            Assert.Equal(sourceRecoveryWork, await ReadRecoveryWorkAsync(sourceConnection));
        }
        finally
        {
            await fixture.CleanupAsync();
        }
    }

    private static async Task SeedMemoryAndActionProjectionAsync(
        string connectionString,
        ActionApprovalOriginFixture origin)
    {
        var memoryItemId = Guid.NewGuid();
        var memoryChunkId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var pendingJobId = Guid.NewGuid();
        var approvedActionId = Guid.NewGuid();
        var approvedArtifactId = Guid.NewGuid();
        var pendingIntentId = Guid.NewGuid();
        await ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_jobs (
                id, fault_id, status, attempt, config_hash, created_at_utc, updated_at_utc)
            VALUES (@pending_job_id, @fault_id, 'Pending', 1, @config_hash,
                    clock_timestamp(), clock_timestamp());

            INSERT INTO incidentcompass.memory_items (
                id, tenant_id, kind, source, title, content, content_hash, version, tags,
                service_name, component, release_name, is_active, seed_managed, seed_owner,
                seed_generation, created_at_utc, updated_at_utc)
            VALUES (@memory_item_id, @tenant_id, 'runbook', 'runbooks/recovery.md', 'Recovery',
                    'bounded recovery evidence', 'memory-hash', 1, ARRAY['recovery'],
                    'orders', 'api', 'v0.3.0', true, true, 'production', gen_random_uuid(),
                    clock_timestamp(), clock_timestamp());

            INSERT INTO incidentcompass.memory_chunks (
                id, memory_item_id, tenant_id, chunk_position, text, text_hash,
                embedding_provider, embedding_model, embedding_dimensions,
                embedding_values, embedding_vector, created_at_utc)
            VALUES (@memory_chunk_id, @memory_item_id, @tenant_id, 0, 'bounded recovery evidence',
                    'chunk-hash', 'openai-compatible', 'production-embedding-model', 2,
                    ARRAY[0.1, 0.2]::real[], '[0.1,0.2]'::vector, clock_timestamp());

            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (@artifact_id, @job_id, 1, 'ProposedAction', @action_ref,
                    '{"category":"ticket_create"}'::jsonb, 'action-artifact-hash', clock_timestamp());

            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (@approved_artifact_id, @job_id, 1, 'ProposedAction', @approved_action_ref,
                    '{"category":"ticket_create"}'::jsonb, 'approved-action-artifact-hash', clock_timestamp());

            INSERT INTO incidentcompass.action_approvals (
                id, tenant_id, origin_report_id, fault_id, job_id, attempt, tool_id, proposal_key,
                category, mode, logical_target_id, adapter_binding_fingerprint,
                approval_contract_version, provenance_sha256, state, canonical_payload,
                payload_sha256, approval_sha256, proposal_artifact_id, review_summary,
                created_at_utc, expires_at_utc, decision_actor, decision_at_utc,
                dispatch_owner, dispatch_fence, dispatch_started_at, dispatch_deadline_at,
                result_payload, result_summary, completed_at_utc,
                external_resource_kind, external_resource_id, external_before_state, external_after_state)
            VALUES (@action_id, @tenant_id, @report_id, @fault_id, @job_id, 1,
                    'ticket_create', 'recovery-proposal', 'ticket_create', 'live',
                    'ticket:configured-repository', repeat('a', 64), 1, repeat('b', 64),
                    'executed', decode('7b7d', 'hex'), repeat('c', 64), repeat('d', 64),
                    @artifact_id, 'Recovery approval projection', clock_timestamp(),
                    clock_timestamp() + interval '1 hour', 'operator', clock_timestamp(),
                    'worker', gen_random_uuid(), clock_timestamp(), clock_timestamp() + interval '1 minute',
                    decode('7b7d', 'hex'), 'Created issue 42', clock_timestamp(),
                    'github_issue', '42', 'absent', 'open');

            INSERT INTO incidentcompass.action_approvals (
                id, tenant_id, origin_report_id, fault_id, job_id, attempt, tool_id, proposal_key,
                category, mode, logical_target_id, adapter_binding_fingerprint,
                approval_contract_version, provenance_sha256, state, canonical_payload,
                payload_sha256, approval_sha256, proposal_artifact_id, review_summary,
                created_at_utc, expires_at_utc, decision_actor, decision_at_utc)
            VALUES (@approved_action_id, @tenant_id, @report_id, @fault_id, @job_id, 1,
                    'ticket_create', 'recovery-approved-undispatched', 'ticket_create', 'live',
                    'github:owner/repository', repeat('e', 64), 1, repeat('f', 64),
                    'approved', convert_to('{"title":"Restored approved action"}', 'UTF8'),
                    repeat('1', 64), repeat('2', 64), @approved_artifact_id,
                    'Approved before recovery', clock_timestamp(), clock_timestamp() + interval '1 hour',
                    'operator', clock_timestamp());

            INSERT INTO incidentcompass.post_report_action_intents (
                id, tenant_id, origin_report_id, fault_id, job_id, attempt, tool_id,
                workflow_version, route_id, config_hash, proposal_key, workflow_input,
                state, created_at_utc)
            VALUES (@pending_intent_id, @tenant_id, @report_id, @fault_id, @job_id, 1,
                    'ticket_create', 1, NULL, @config_hash, 'recovery-pending-intent',
                    convert_to('{}', 'UTF8'), 'pending', clock_timestamp());
            """,
            ("pending_job_id", pendingJobId),
            ("memory_item_id", memoryItemId),
            ("memory_chunk_id", memoryChunkId),
            ("tenant_id", origin.TenantId),
            ("artifact_id", artifactId),
            ("job_id", origin.JobId),
            ("config_hash", origin.ConfigHash),
            ("approved_artifact_id", approvedArtifactId),
            ("approved_action_ref", "action:" + approvedActionId),
            ("action_ref", "action:" + actionId),
            ("action_id", actionId),
            ("approved_action_id", approvedActionId),
            ("pending_intent_id", pendingIntentId),
            ("report_id", origin.ReportId),
            ("fault_id", origin.FaultId));
    }

    private static async Task ConvertMigrationLedgerToV03LegacyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // A v0.3-era ledger can only carry legacy CRLF checksums for migrations that shipped in a
        // release. Unreleased catalog entries have no legacy form to convert, so the released flag
        // on the catalog entry decides which rows are rewritten instead of a version number.
        var releasedMigrations = PostgresMigrationCatalog.All
            .Where(migration => migration.AcceptsReleasedLegacyChecksums)
            .ToArray();
        Assert.NotEmpty(releasedMigrations);
        foreach (var migration in releasedMigrations)
        {
            var policy = await PostgresMigrationChecksumPolicy.CreateAsync(
                migration, TestContext.Current.CancellationToken);
            Assert.True(policy.ReleasedLegacyCrlfChecksum.HasValue);
            await using var command = new NpgsqlCommand("""
                UPDATE incidentcompass.schema_migrations SET checksum = $1 WHERE version = $2;
                """, connection);
            command.Parameters.AddWithValue(policy.ReleasedLegacyCrlfChecksum.Value.Value);
            command.Parameters.AddWithValue(migration.Version);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<Dictionary<string, long>> ReadCountsAsync(string connectionString)
    {
        var queries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reports"] = "SELECT count(*) FROM incidentcompass.triage_reports;",
            ["evidence"] = "SELECT count(*) FROM incidentcompass.triage_evidence;",
            ["ledger"] = "SELECT count(*) FROM incidentcompass.triage_ledger;",
            ["approvals"] = "SELECT count(*) FROM incidentcompass.action_approvals;",
            ["actionProjection"] = "SELECT count(*) FROM incidentcompass.action_approvals WHERE external_resource_kind IS NOT NULL;",
            ["memoryItems"] = "SELECT count(*) FROM incidentcompass.memory_items;",
            ["memoryChunks"] = "SELECT count(*) FROM incidentcompass.memory_chunks;",
            ["migrationHistory"] = "SELECT count(*) FROM incidentcompass.schema_migrations WHERE status = 'Applied';"
        };
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            counts[query.Key] = await ActionApprovalTestSupport.CountAsync(connectionString, query.Value);
        }
        return counts;
    }

    private static async Task<Dictionary<string, long>> ReadRecoveryWorkAsync(string connectionString)
    {
        var queries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pendingTriageJobs"] = "SELECT count(*) FROM incidentcompass.triage_jobs WHERE status = 'Pending';",
            ["approvedUndispatchedActions"] = "SELECT count(*) FROM incidentcompass.action_approvals WHERE state = 'approved' AND dispatch_started_at IS NULL;",
            ["pendingPostReportIntents"] = "SELECT count(*) FROM incidentcompass.post_report_action_intents WHERE state = 'pending';"
        };
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            counts[query.Key] = await ActionApprovalTestSupport.CountAsync(connectionString, query.Value);
        }
        return counts;
    }

    private static async Task AssertNoWorkerContainerAsync(string project)
    {
        var worker = await RunProcessAsync(
            "docker", null, "ps", "--all", "--quiet",
            "--filter", $"label=com.docker.compose.project={project}",
            "--filter", "label=com.docker.compose.service=worker");
        AssertSuccess(worker);
        Assert.True(string.IsNullOrWhiteSpace(worker.StandardOutput));
    }

    private static async Task<string[]> ReadMigrationChecksumsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT version || ':' || checksum FROM incidentcompass.schema_migrations ORDER BY version;", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }
        return rows.ToArray();
    }

    private static void AssertSuccess(ProcessResult result)
    {
        Assert.True(result.ExitCode == 0, $"Process failed. stdout: {result.StandardOutput}\nstderr: {result.StandardError}");
    }

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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        await process.WaitForExitAsync(timeout.Token);
        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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

    private sealed class RecoveryFixture : IAsyncDisposable
    {
        private RecoveryFixture(string repositoryRoot, string tempDirectory, Dictionary<string, string> settings)
        {
            RepositoryRoot = repositoryRoot;
            TempDirectory = tempDirectory;
            Settings = settings;
            EnvironmentFile = Path.Combine(tempDirectory, ".env.production");
            BackupDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory, "backups")).FullName;
            TimeoutBackupDirectory = Directory.CreateDirectory(Path.Combine(tempDirectory, "timeout-backups")).FullName;
            SourceProject = settings["INCIDENTCOMPASS_COMPOSE_PROJECT"];
            TargetProject = SourceProject + "-recovery";
            FailedTargetProject = SourceProject + "-failed";
            SourceApiPort = int.Parse(settings["IC_API_PORT"], CultureInfo.InvariantCulture);
            SourcePostgresPort = int.Parse(settings["IC_POSTGRES_PORT"], CultureInfo.InvariantCulture);
            TargetApiPort = GetFreePort();
            TargetPostgresPort = GetFreePort();
            FailedTargetApiPort = GetFreePort();
            FailedTargetPostgresPort = GetFreePort();
        }

        public string RepositoryRoot { get; }
        public string TempDirectory { get; }
        public string EnvironmentFile { get; }
        public string BackupDirectory { get; }
        public string TimeoutBackupDirectory { get; }
        public Dictionary<string, string> Settings { get; }
        public string SourceProject { get; }
        public string TargetProject { get; }
        public string FailedTargetProject { get; }
        public int SourceApiPort { get; }
        public int SourcePostgresPort { get; }
        public int TargetApiPort { get; }
        public int TargetPostgresPort { get; }
        public int FailedTargetApiPort { get; }
        public int FailedTargetPostgresPort { get; }

        public static async Task<RecoveryFixture> CreateAsync()
        {
            var root = FindRepositoryRoot();
            var temp = Directory.CreateTempSubdirectory("incidentcompass-recovery-").FullName;
            var source = Directory.CreateDirectory(Path.Combine(temp, "source")).FullName;
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["INCIDENTCOMPASS_COMPOSE_PROJECT"] = "ic-recovery-" + suffix,
                ["INCIDENTCOMPASS_IMAGE_TAG"] = "recovery-test-" + suffix,
                ["IC_API_PORT"] = GetFreePort().ToString(CultureInfo.InvariantCulture),
                ["IC_POSTGRES_PORT"] = GetFreePort().ToString(CultureInfo.InvariantCulture),
                ["POSTGRES_DB"] = "ic_recovery_test",
                ["POSTGRES_USER"] = "ic_recovery_operator",
                ["POSTGRES_PASSWORD"] = "database-secret-value-12345",
                ["INCIDENTCOMPASS_API_KEY_AUTH_ENABLED"] = "true",
                ["INCIDENTCOMPASS_API_KEY_ID"] = "primary-operator",
                ["INCIDENTCOMPASS_TENANT_ID"] = "production-test",
                ["INCIDENTCOMPASS_API_KEY_SHA256"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                ["INCIDENTCOMPASS_LLM_BASE_URL"] = "https://provider.test",
                ["INCIDENTCOMPASS_LLM_CHAT_COMPLETIONS_PATH"] = "/v1/chat/completions",
                ["INCIDENTCOMPASS_LLM_MODEL"] = "production-chat-model",
                ["INCIDENTCOMPASS_LLM_API_KEY"] = "model-secret-value-12345",
                ["INCIDENTCOMPASS_EMBEDDINGS_BASE_URL"] = "https://provider.test",
                ["INCIDENTCOMPASS_EMBEDDINGS_PATH"] = "/v1/embeddings",
                ["INCIDENTCOMPASS_EMBEDDINGS_MODEL"] = "production-embedding-model",
                ["INCIDENTCOMPASS_EMBEDDINGS_API_KEY"] = "embedding-secret-value-12345",
                ["INCIDENTCOMPASS_ALLOW_INSECURE_LOOPBACK_PROVIDER"] = "false",
                ["INCIDENTCOMPASS_SOURCE_ROOT"] = source,
                ["INCIDENTCOMPASS_SOURCE_SERVICE"] = "orders",
                ["INCIDENTCOMPASS_SOURCE_RELEASE"] = "v0.3.0",
                ["INCIDENTCOMPASS_GITHUB_OWNER"] = "owner",
                ["INCIDENTCOMPASS_GITHUB_REPOSITORY"] = "repository",
                ["INCIDENTCOMPASS_GITHUB_TOKEN"] = "github-secret-value-12345",
                ["INCIDENTCOMPASS_MEMORY_SEED_ENABLED"] = "false",
                ["INCIDENTCOMPASS_TELEGRAM_ENABLED"] = "false"
            };
            var fixture = new RecoveryFixture(root, temp, settings);
            await File.WriteAllLinesAsync(fixture.EnvironmentFile, settings.Select(entry => $"{entry.Key}={entry.Value}"));
            return fixture;
        }

        public string ConnectionString(int port) =>
            $"Host=127.0.0.1;Port={port};Database={Settings["POSTGRES_DB"]};" +
            $"Username={Settings["POSTGRES_USER"]};Password={Settings["POSTGRES_PASSWORD"]}";

        public async Task<ProcessResult> RunComposeAsync(params string[] tail) =>
            await RunProcessAsync("docker", Settings,
            [
                "compose", "-f", Path.Combine(RepositoryRoot, "docker-compose.yml"),
                "-f", Path.Combine(RepositoryRoot, "compose.production.yml"),
                "--env-file", EnvironmentFile, "--project-name", SourceProject,
                .. tail
            ]);

        public async Task<ProcessResult> RunPowerShellAsync(string script, params string[] arguments) =>
            await RunProcessAsync("pwsh", null,
            [
                "-NoProfile", "-File", Path.Combine(RepositoryRoot, "scripts", script),
                .. arguments
            ]);

        public async Task AssertRuntimeContainersAsync()
        {
            foreach (var service in new[] { "api", "worker", "postgres" })
            {
                var port = service == "api" ? "8080" : service == "postgres" ? "5432" : null;
                if (port is not null)
                {
                    var binding = await RunComposeAsync("port", service, port);
                    AssertSuccess(binding);
                    Assert.StartsWith("127.0.0.1:", binding.StandardOutput.Trim(), StringComparison.Ordinal);
                }

                var container = await RunComposeAsync("ps", "--quiet", service);
                AssertSuccess(container);
                if (service == "postgres")
                {
                    var processes = await RunProcessAsync(
                        "docker", null, "top", container.StandardOutput.Trim(), "-eo", "user,pid,comm");
                    AssertSuccess(processes);
                    var processLines = processes.StandardOutput.Split(
                        ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    Assert.True(processLines.Length > 1);
                    var mainProcess = processLines[1].Trim();
                    Assert.False(mainProcess.StartsWith("0 ", StringComparison.Ordinal));
                    Assert.EndsWith("postgres", mainProcess, StringComparison.Ordinal);
                }
                else
                {
                    var user = await RunComposeAsync("exec", "--no-TTY", service, "id", "-u");
                    AssertSuccess(user);
                    Assert.NotEqual("0", user.StandardOutput.Trim());
                }
                var inspect = await RunProcessAsync("docker", null, "inspect", "--format",
                    "{{.State.Health.Status}}|{{.HostConfig.LogConfig.Type}}|{{.HostConfig.RestartPolicy.Name}}",
                    container.StandardOutput.Trim());
                AssertSuccess(inspect);
                Assert.Equal("healthy|json-file|unless-stopped", inspect.StandardOutput.Trim());
            }
        }

        public async Task InstallSlowPgDumpAsync()
        {
            var container = await RunComposeAsync("ps", "--quiet", "postgres");
            AssertSuccess(container);
            var containerId = container.StandardOutput.Trim();
            var location = await RunComposeAsync(
                "exec", "--no-TTY", "postgres", "sh", "-c", "command -v pg_dump");
            AssertSuccess(location);
            var pgDumpPath = location.StandardOutput.Trim();
            Assert.StartsWith("/", pgDumpPath, StringComparison.Ordinal);

            var wrapper = Path.Combine(TempDirectory, "slow-pg-dump");
            await File.WriteAllTextAsync(
                wrapper,
                $"#!/bin/sh\nsleep 10\nexec {pgDumpPath}.real \"$@\"\n");
            AssertSuccess(await RunProcessAsync(
                "docker", null, "exec", "--user", "0", containerId,
                "mv", pgDumpPath, pgDumpPath + ".real"));
            AssertSuccess(await RunProcessAsync(
                "docker", null, "cp", wrapper, containerId + ":" + pgDumpPath));
            AssertSuccess(await RunProcessAsync(
                "docker", null, "exec", "--user", "0", containerId,
                "chmod", "755", pgDumpPath));
        }

        public async Task<string> CreateMalformedBackupPairAsync()
        {
            var dump = Path.Combine(BackupDirectory,
                "incidentcompass-backup-20260903T020000000Z-feedbeef.dump");
            await File.WriteAllTextAsync(dump, "not-a-postgresql-custom-dump");
            var bytes = await File.ReadAllBytesAsync(dump);
            await File.WriteAllTextAsync(dump[..^5] + ".manifest.json", JsonSerializer.Serialize(new
            {
                kind = "IncidentCompass.PostgresBackup",
                formatVersion = 1,
                createdAtUtc = DateTimeOffset.Parse("2026-09-03T02:00:00Z", CultureInfo.InvariantCulture),
                dumpFile = Path.GetFileName(dump),
                sizeBytes = bytes.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                postgresFormat = "custom"
            }));
            return dump;
        }

        public void AssertSecretsAbsent(ProcessResult result)
        {
            var combined = result.StandardOutput + result.StandardError;
            foreach (var key in new[]
                     {
                         "POSTGRES_PASSWORD", "INCIDENTCOMPASS_LLM_API_KEY",
                         "INCIDENTCOMPASS_EMBEDDINGS_API_KEY", "INCIDENTCOMPASS_GITHUB_TOKEN"
                     })
            {
                Assert.DoesNotContain(Settings[key], combined, StringComparison.Ordinal);
            }
        }

        public async Task CleanupAsync()
        {
            foreach (var project in new[] { FailedTargetProject, TargetProject, SourceProject })
            {
                var environment = new Dictionary<string, string>(Settings, StringComparer.Ordinal)
                {
                    ["INCIDENTCOMPASS_COMPOSE_PROJECT"] = project
                };
                await RunProcessAsync("docker", environment,
                    "compose", "-f", Path.Combine(RepositoryRoot, "docker-compose.yml"),
                    "-f", Path.Combine(RepositoryRoot, "compose.production.yml"),
                    "--env-file", EnvironmentFile, "--project-name", project,
                    "--profile", "recovery", "down", "--volumes", "--remove-orphans");
            }

            await RunProcessAsync(
                "docker", null, "image", "rm", "--force",
                $"incidentcompass-api:{Settings["INCIDENTCOMPASS_IMAGE_TAG"]}",
                $"incidentcompass-worker:{Settings["INCIDENTCOMPASS_IMAGE_TAG"]}");
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(TempDirectory))
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }
}

[CollectionDefinition("Production recovery", DisableParallelization = true)]
public sealed class ProductionRecoveryCollection;
