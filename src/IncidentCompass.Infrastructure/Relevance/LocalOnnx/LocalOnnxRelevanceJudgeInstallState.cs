using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// The relevance judge's own install snapshot, held apart from the embedding model's.
/// <para>
/// A <see cref="LocalOnnxModelInstallState" /> carries one install pass's outcome and says nothing
/// about which model that pass installed, so the judge and the embedding model must never share one
/// instance. Sharing would break it in both directions: the judge would read the embedding model's
/// snapshot and report the wrong model as installed, and the judge's own
/// <see cref="LocalOnnxModelInstallState.RecordInstalled" /> would overwrite the state the embedding
/// adapter reads, pointing that adapter at a model that is not an embedding model at all. This type
/// exists to give the judge's state a service identity of its own; it adds no behavior.
/// </para>
/// </summary>
internal sealed class LocalOnnxRelevanceJudgeInstallState : LocalOnnxModelInstallState;
