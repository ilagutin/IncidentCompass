using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.ML.OnnxRuntime;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// One installed cross-encoder loaded into ONNX Runtime together with its tokenizer. Not safe for
/// concurrent use on its own; <see cref="LocalOnnxRelevanceJudgeRuntime" /> serializes every call.
/// </summary>
internal sealed class LocalOnnxLoadedRelevanceJudge : IDisposable
{
    public const string InputIdsName = "input_ids";
    public const string AttentionMaskName = "attention_mask";
    public const string TokenTypeIdsName = "token_type_ids";
    public const string LogitsName = "logits";

    private static readonly string[] OutputNames = [LogitsName];

    private readonly InferenceSession session;
    private readonly LocalOnnxPairEncoder encoder;
    private readonly bool feedsTokenTypeIds;

    private LocalOnnxLoadedRelevanceJudge(
        LocalOnnxInstalledModel installed,
        InferenceSession session,
        LocalOnnxPairEncoder encoder)
    {
        Installed = installed;
        this.session = session;
        this.encoder = encoder;
        feedsTokenTypeIds = session.InputMetadata.ContainsKey(TokenTypeIdsName);
    }

    public LocalOnnxInstalledModel Installed { get; }

    internal LocalOnnxPairEncoder Encoder => encoder;

    public static LocalOnnxLoadedRelevanceJudge Load(LocalOnnxInstalledModel installed, int intraOpThreads)
    {
        ArgumentNullException.ThrowIfNull(installed);
        LocalOnnxPairEncoder encoder;
        InferenceSession? session = null;
        try
        {
            encoder = LocalOnnxPairEncoder.Load(installed.TokenizerFilePath, installed.Manifest);
            using var sessionOptions = new SessionOptions
            {
                IntraOpNumThreads = intraOpThreads,
                InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };
            session = new InferenceSession(installed.ModelFilePath, sessionOptions);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            session?.Dispose();
            throw LocalOnnxRelevanceJudgeErrors.LoadFailed(exception.GetType().Name + ".", exception);
        }

        if (!session.InputMetadata.ContainsKey(InputIdsName) ||
            !session.InputMetadata.ContainsKey(AttentionMaskName) ||
            !session.OutputMetadata.ContainsKey(LogitsName))
        {
            session.Dispose();
            throw LocalOnnxRelevanceJudgeErrors.LoadFailed(
                $"the graph does not declare the inputs {InputIdsName} and {AttentionMaskName} and the output" +
                $" {LogitsName}.");
        }

        return new LocalOnnxLoadedRelevanceJudge(installed, session, encoder);
    }

    /// <summary>
    /// The model's single logit for one pair. Higher means more relevant, on the model's own scale.
    /// </summary>
    public float Score(string query, string passage, CancellationToken cancellationToken)
    {
        try
        {
            return ScoreCore(query, passage, cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException and not MemoryRelevanceJudgeException)
        {
            // Every other failure on the inference path, whether from the tokenizer, the tensor
            // build, the run or the output's element type, leaves as the port's exception with one
            // code, the same way a load failure does.
            throw LocalOnnxRelevanceJudgeErrors.InferenceFailed(exception);
        }
    }

    public void Dispose() => session.Dispose();

    private float ScoreCore(string query, string passage, CancellationToken cancellationToken)
    {
        var inputIds = encoder.Encode(query, passage);
        var shape = new long[] { 1, inputIds.Length };
        var attentionMask = new long[inputIds.Length];
        Array.Fill(attentionMask, 1L);

        using var inputIdsValue = OrtValue.CreateTensorValueFromMemory(inputIds, shape);
        using var attentionMaskValue = OrtValue.CreateTensorValueFromMemory(attentionMask, shape);

        // The reference tokenizer emits no token type ids for this layout. A graph that declares the
        // input still has to be fed one, so it is fed zeros, exactly as the embedding adapter does.
        using var tokenTypeIdsValue = feedsTokenTypeIds
            ? OrtValue.CreateTensorValueFromMemory(new long[inputIds.Length], shape)
            : null;
        var inputs = new Dictionary<string, OrtValue>(StringComparer.Ordinal)
        {
            [InputIdsName] = inputIdsValue,
            [AttentionMaskName] = attentionMaskValue
        };
        if (tokenTypeIdsValue is not null)
        {
            inputs[TokenTypeIdsName] = tokenTypeIdsValue;
        }

        using var runOptions = new RunOptions();
        using var outputs = Run(runOptions, inputs, cancellationToken);
        var logits = outputs[0];
        var logitsShape = logits.GetTensorTypeAndShape().Shape;
        if (logitsShape.Length != 2 || logitsShape[0] != 1 || logitsShape[1] != 1)
        {
            throw LocalOnnxRelevanceJudgeErrors.OutputShapeInvalid(logitsShape);
        }

        return logits.GetTensorDataAsSpan<float>()[0];
    }

    /// <summary>
    /// A cancelled call stops the running inference instead of waiting for it: the token sets the
    /// run's terminate flag, ONNX Runtime stops at its next check and throws, and that failure is
    /// reported as the cancellation it is.
    /// </summary>
    private IDisposableReadOnlyCollection<OrtValue> Run(
        RunOptions runOptions,
        IReadOnlyDictionary<string, OrtValue> inputs,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(
            static state => ((RunOptions)state!).Terminate = true,
            runOptions);
        try
        {
            return session.Run(runOptions, inputs, OutputNames);
        }
        catch (OnnxRuntimeException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The relevance judge call was cancelled while the model was running.",
                exception,
                cancellationToken);
        }
    }
}
