using IncidentCompass.Tester;
using IncidentCompass.Tester.Evaluation;

if (args.Contains("--evaluation", StringComparer.OrdinalIgnoreCase))
{
    var evaluationOptions = EvaluationOptions.Parse(args);
    using var evaluationCancellation = new EvaluationCancellationSource();
    using var evaluationClient = new HttpClient
    {
        BaseAddress = evaluationOptions.BaseUrl,
        Timeout = evaluationOptions.RequestTimeout
    };
    try
    {
        return await new EvaluationRunner(evaluationClient, evaluationOptions)
            .RunAsync(evaluationCancellation.Token);
    }
    catch (OperationCanceledException) when (evaluationCancellation.Token.IsCancellationRequested)
    {
        Console.Error.WriteLine("Evaluation cancelled after retaining the latest bounded checkpoint.");
        return 130;
    }
}

var options = TesterOptions.Parse(
    args,
    Environment.GetEnvironmentVariable("INCIDENTCOMPASS_TESTER_BASE_URL"),
    Environment.GetEnvironmentVariable("INCIDENTCOMPASS_TESTER_PUBLIC_BASE_URL"),
    Environment.GetEnvironmentVariable("INCIDENTCOMPASS_TESTER_REQUEST_TIMEOUT_SECONDS"),
    Environment.GetEnvironmentVariable("INCIDENTCOMPASS_TESTER_POLL_TIMEOUT_SECONDS"),
    Environment.GetEnvironmentVariable("INCIDENTCOMPASS_TESTER_POLL_INTERVAL_SECONDS"));
using var client = new HttpClient
{
    BaseAddress = options.BaseUrl,
    Timeout = options.RequestTimeout
};

var tester = new DemoTester(client, options);
return await tester.RunAsync(CancellationToken.None);
