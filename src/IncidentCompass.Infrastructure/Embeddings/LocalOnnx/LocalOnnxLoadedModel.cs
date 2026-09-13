using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.ML.OnnxRuntime;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>
/// One installed model loaded into ONNX Runtime together with its tokenizer. Not safe for concurrent
/// use on its own; <see cref="LocalOnnxModelRuntime" /> serializes every call to it.
/// </summary>
internal sealed class LocalOnnxLoadedModel : IDisposable
{
    public const string InputIdsName = "input_ids";
    public const string AttentionMaskName = "attention_mask";
    public const string TokenTypeIdsName = "token_type_ids";
    public const string LastHiddenStateName = "last_hidden_state";

    private static readonly string[] OutputNames = [LastHiddenStateName];

    private readonly InferenceSession session;
    private readonly LocalOnnxInputEncoder encoder;
    private readonly bool feedsTokenTypeIds;

    private LocalOnnxLoadedModel(
        LocalOnnxInstalledModel installed,
        InferenceSession session,
        LocalOnnxInputEncoder encoder)
    {
        Installed = installed;
        this.session = session;
        this.encoder = encoder;
        feedsTokenTypeIds = session.InputMetadata.ContainsKey(TokenTypeIdsName);
    }

    public LocalOnnxInstalledModel Installed { get; }

    public static LocalOnnxLoadedModel Load(LocalOnnxInstalledModel installed, int intraOpThreads)
    {
        ArgumentNullException.ThrowIfNull(installed);
        LocalOnnxInputEncoder encoder;
        InferenceSession? session = null;
        try
        {
            encoder = LocalOnnxInputEncoder.Load(installed.TokenizerFilePath, installed.Manifest);
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
            throw LocalOnnxEmbeddingErrors.LoadFailed(exception.GetType().Name + ".", exception);
        }

        if (!session.InputMetadata.ContainsKey(InputIdsName) ||
            !session.InputMetadata.ContainsKey(AttentionMaskName) ||
            !session.OutputMetadata.ContainsKey(LastHiddenStateName))
        {
            session.Dispose();
            throw LocalOnnxEmbeddingErrors.LoadFailed(
                $"the graph does not declare the inputs {InputIdsName} and {AttentionMaskName} and the output" +
                $" {LastHiddenStateName}.");
        }

        return new LocalOnnxLoadedModel(installed, session, encoder);
    }

    public (float[] Vector, int InputTokens) Embed(string input, EmbeddingInputKind kind, CancellationToken cancellationToken)
    {
        try
        {
            return EmbedCore(input, kind, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not EmbeddingClientException)
        {
            // Every other failure on the inference path, whether from the tokenizer, the tensor build,
            // the run, the output's element type or shape, or pooling, leaves as the port's exception
            // with one code, the same way a load failure does.
            throw LocalOnnxEmbeddingErrors.InferenceFailed(exception);
        }
    }

    public void Dispose() => session.Dispose();

    private (float[] Vector, int InputTokens) EmbedCore(
        string input,
        EmbeddingInputKind kind,
        CancellationToken cancellationToken)
    {
        var inputIds = encoder.Encode(input, kind);
        var shape = new long[] { 1, inputIds.Length };
        var attentionMask = new long[inputIds.Length];
        Array.Fill(attentionMask, 1L);

        using var inputIdsValue = OrtValue.CreateTensorValueFromMemory(inputIds, shape);
        using var attentionMaskValue = OrtValue.CreateTensorValueFromMemory(attentionMask, shape);
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
        var hiddenStates = outputs[0];
        var dimensions = checked((int)hiddenStates.GetTensorTypeAndShape().Shape[^1]);
        var manifest = Installed.Manifest;
        if (dimensions != manifest.Dimensions)
        {
            throw LocalOnnxEmbeddingErrors.DimensionsMismatch(dimensions, manifest.Dimensions);
        }

        var vector = LocalOnnxVectorPooling.MeanPool(hiddenStates.GetTensorDataAsSpan<float>(), attentionMask, dimensions);
        if (manifest.Normalize)
        {
            LocalOnnxVectorPooling.NormalizeInPlace(vector);
        }

        return (vector, inputIds.Length);
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
                "The embedding call was cancelled while the model was running.",
                exception,
                cancellationToken);
        }
    }
}
