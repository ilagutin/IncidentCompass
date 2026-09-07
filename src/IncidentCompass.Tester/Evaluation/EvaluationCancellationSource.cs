using System.Runtime.InteropServices;

namespace IncidentCompass.Tester.Evaluation;

internal sealed class EvaluationCancellationSource : IDisposable
{
    private readonly CancellationTokenSource source = new();
    private readonly PosixSignalRegistration? terminationRegistration;

    public EvaluationCancellationSource()
    {
        Console.CancelKeyPress += OnConsoleCancel;
        if (!OperatingSystem.IsWindows())
        {
            terminationRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTermination);
        }
    }

    public CancellationToken Token => source.Token;

    public void Dispose()
    {
        Console.CancelKeyPress -= OnConsoleCancel;
        terminationRegistration?.Dispose();
        source.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnConsoleCancel(object? sender, ConsoleCancelEventArgs args)
    {
        args.Cancel = true;
        source.Cancel();
    }

    private void OnTermination(PosixSignalContext context)
    {
        context.Cancel = true;
        source.Cancel();
    }
}
