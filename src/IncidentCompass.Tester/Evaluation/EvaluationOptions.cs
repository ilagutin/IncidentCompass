namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationOptions(
    Uri BaseUrl,
    TimeSpan RequestTimeout,
    TimeSpan AttemptTimeout,
    TimeSpan RecoveryTimeout,
    TimeSpan PollInterval,
    string CorpusPath,
    string ConfigurationPath,
    string OutputPath,
    int RunsPerCase,
    string EvaluatedRevision,
    string EvaluatedContentIdentity,
    bool EvaluatedContentDirty,
    EvaluationConfigurationSnapshot Configuration,
    bool EmptyActionGrants,
    bool ExternalActionCredentialsProvided)
{
    public static EvaluationOptions Parse(string[] args)
    {
        var runs = ReadInt(args, "--evaluation-runs", "INCIDENTCOMPASS_TESTER_EVALUATION_RUNS", 3);
        var corpus = Read(args, "--evaluation-corpus", "INCIDENTCOMPASS_TESTER_EVALUATION_CORPUS")
            ?? "/app/evaluations/triage/corpus-v1.json";
        var configurationPath = Read(args, "--evaluation-config", "INCIDENTCOMPASS_TESTER_EVALUATION_CONFIG")
            ?? "/app/evaluations/triage/incidentcompass.config.json";
        var options = new EvaluationOptions(
            BaseUrl: ToBaseUri(Read(args, "--base-url", "INCIDENTCOMPASS_TESTER_BASE_URL") ?? "http://localhost:5198"),
            RequestTimeout: TimeSpan.FromSeconds(ReadInt(args, "--request-timeout-seconds", "INCIDENTCOMPASS_TESTER_REQUEST_TIMEOUT_SECONDS", 30)),
            AttemptTimeout: TimeSpan.FromSeconds(ReadInt(args, "--attempt-timeout-seconds", "INCIDENTCOMPASS_TESTER_EVALUATION_ATTEMPT_TIMEOUT_SECONDS", 240)),
            RecoveryTimeout: TimeSpan.FromSeconds(ReadInt(args, "--recovery-timeout-seconds", "INCIDENTCOMPASS_TESTER_EVALUATION_RECOVERY_TIMEOUT_SECONDS", 5)),
            PollInterval: TimeSpan.FromSeconds(ReadInt(args, "--poll-interval-seconds", "INCIDENTCOMPASS_TESTER_POLL_INTERVAL_SECONDS", 2)),
            CorpusPath: corpus,
            ConfigurationPath: configurationPath,
            OutputPath: Read(args, "--evaluation-output", "INCIDENTCOMPASS_TESTER_EVALUATION_OUTPUT") ?? "/artifacts/triage-evaluation-result-v2.json",
            RunsPerCase: runs,
            EvaluatedRevision: Read(args, "--evaluation-revision", "INCIDENTCOMPASS_TESTER_EVALUATION_REVISION") ?? "unknown",
            EvaluatedContentIdentity: Read(args, "--evaluation-content-identity", "INCIDENTCOMPASS_TESTER_EVALUATION_CONTENT_IDENTITY") ?? "unknown",
            EvaluatedContentDirty: ReadBool("INCIDENTCOMPASS_TESTER_EVALUATION_CONTENT_DIRTY"),
            Configuration: EvaluationConfigurationSnapshotLoader.Load(configurationPath),
            EmptyActionGrants: ReadBool("INCIDENTCOMPASS_TESTER_EVALUATION_EMPTY_ACTION_GRANTS"),
            ExternalActionCredentialsProvided: ReadBool("INCIDENTCOMPASS_TESTER_EVALUATION_EXTERNAL_ACTION_CREDENTIALS"));

        _ = EvaluationCorpus.Load(options.CorpusPath);
        if (runs != EvaluationCorpus.RequiredAttemptsPerCase)
        {
            throw new ArgumentException("--evaluation-runs must be exactly 3 for the version 1 evaluation contract.");
        }

        return options;
    }

    private static string? Read(string[] args, string optionName, string environmentName)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return Environment.GetEnvironmentVariable(environmentName);
    }

    private static int ReadInt(string[] args, string optionName, string environmentName, int fallback)
    {
        var value = Read(args, optionName, environmentName);
        if (value is null)
        {
            return fallback;
        }

        return int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException(optionName + " must be a positive integer.");
    }

    private static bool ReadBool(string environmentName) =>
        bool.TryParse(Environment.GetEnvironmentVariable(environmentName), out var value) && value;

    private static Uri ToBaseUri(string value) => new(value.TrimEnd('/') + "/");
}
