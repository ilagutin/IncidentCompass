namespace IncidentCompass.IntegrationTests;

/// <summary>
/// One relevance-judge benchmark run. It carries what a threshold decision needs: the measured result at
/// the shipped defaults, the full curve of both thresholds swept independently, the scores the floor and
/// the confirm score actually sit between, and the attack legs that show whether a model can raise a
/// band by choosing its query.
/// </summary>
/// <remarks>
/// Schema version 3 runs on the version 3 corpus, whose every query carries its trigger signal, and
/// decides every band against the fault query built from it. It added the fault-score boundaries to
/// <c>Scoring</c>, the incident-shaped positive counts to every language and <c>AttackLegs</c>. Schema
/// version 2 added <c>Scoring</c> and <c>Sweeps</c>. Version 1 recorded only the shipped-default legs,
/// which was enough to check a threshold and not enough to choose one.
/// </remarks>
public sealed record MemorySearchRelevanceJudgeBenchmarkRecord(
    int SchemaVersion,
    string Benchmark,
    string CorpusVersion,
    string MultilingualQueriesVersion,
    string EmbeddingModel,
    string JudgeModel,
    string JudgeRuntime,
    double ShippedConfirmScore,
    double ShippedFloorScore,
    double MinScore,
    int TopK,
    int CandidateCount,
    string OperatingSystem,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    MemorySearchRelevanceJudgeBenchmarkScoring Scoring,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkLeg> Legs,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkSweep> Sweeps,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkAttackLeg> AttackLegs);

/// <summary>
/// What the judge said, before any threshold was applied to it. Every (query, candidate) pair of every
/// language was scored exactly once against the model's query and once against the fault query, and
/// every sweep point reuses those scores, so <c>JudgeModelCallsDuringSweep</c> should be zero: a non-zero
/// value means a sweep run asked about a pair the capture pass did not cover, which makes the sweep slow
/// but not wrong. <c>ByLanguage</c> and <c>Pooled</c> are the floor's boundary over the model-query
/// scores; <c>ConfirmByLanguage</c> and <c>ConfirmPooled</c> are the confirm score's over the fault
/// scores, which are what the confirm score is now compared with.
/// </summary>
public sealed record MemorySearchRelevanceJudgeBenchmarkScoring(
    int ScoredPairCount,
    int JudgeModelCallsDuringCapture,
    int JudgeModelCallsDuringSweep,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkBoundary> ByLanguage,
    MemorySearchRelevanceJudgeBenchmarkBoundary Pooled,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkConfirmBoundary> ConfirmByLanguage,
    MemorySearchRelevanceJudgeBenchmarkConfirmBoundary ConfirmPooled);

/// <summary>
/// The two scores a floor has to fall between, and how much room there is between them.
/// <para>
/// <c>StrongestOffTopicScore</c> is the highest score any off-topic query gave any candidate it was
/// offered, so a floor at or below it lets an off-topic query return something.
/// <c>WeakestRelevantScore</c> is the lowest score of any labelled relevant chunk that its own positive
/// query actually had among its candidates, so a floor above it drops an answer the corpus says is
/// right. <c>WindowWidth</c> is the second minus the first, and <c>SeparableByAFloor</c> is whether it
/// is positive: when it is not, no floor satisfies both halves of the bar and the right answer is to
/// say so rather than to move the bar.
/// </para>
/// <para>
/// <c>RelevantChunksMissingFromCandidates</c> counts labelled relevant chunks the vector search never
/// offered the judge. No threshold can recover those, so a recall shortfall that size is the retriever's
/// and not the judge's.
/// </para>
/// </summary>
public sealed record MemorySearchRelevanceJudgeBenchmarkBoundary(
    string Language,
    double? StrongestOffTopicScore,
    string? StrongestOffTopicQueryId,
    string? StrongestOffTopicChunkId,
    int OffTopicPairCount,
    double? WeakestRelevantScore,
    string? WeakestRelevantQueryId,
    string? WeakestRelevantChunkId,
    int RelevantPairCount,
    int RelevantChunksMissingFromCandidates,
    double? WindowWidth,
    bool SeparableByAFloor);

/// <summary>One configuration of <c>Tools.memory_search.RelevanceJudge</c>, over every language.</summary>
public sealed record MemorySearchRelevanceJudgeBenchmarkLeg(
    string Name,
    string RelevanceJudge,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkLanguage> Languages);

/// <summary>
/// One threshold swept over its own list of values while the other is held at its shipped default,
/// which <c>HeldConfirmScore</c> and <c>HeldFloorScore</c> record. The two are swept independently
/// rather than as a grid: a grid of both lists at this resolution is six figures of runs, and the two
/// thresholds answer different questions, the floor whether anything is returned at all and the confirm
/// score whether what is returned is called confirmed.
/// <para>
/// The measured values are derived from the held threshold rather than fixed, because a floor above the
/// confirm score, or a confirm score below the floor, is a pair load validation refuses and therefore a
/// configuration that cannot exist. <c>CandidateValueCount</c>, <c>ExcludedValues</c> and
/// <c>ExclusionRule</c> record that derivation, so a gap in the curve is visibly a value that was ruled
/// out rather than one that was forgotten.
/// </para>
/// </summary>
public sealed record MemorySearchRelevanceJudgeBenchmarkSweep(
    string Threshold,
    double HeldConfirmScore,
    double HeldFloorScore,
    int CandidateValueCount,
    IReadOnlyList<double> ExcludedValues,
    string ExclusionRule,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkSweepPoint> Points);

public sealed record MemorySearchRelevanceJudgeBenchmarkSweepPoint(
    double Value,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkLanguage> Languages);

/// <summary>
/// One language at one threshold setting. <c>HardNegativeReturnedCount</c> is a diagnostic; the error
/// measures are <c>HardNegativeConfirmedCount</c>, which counts hard negatives the tool banded
/// <c>medium</c> or better, and <c>OffTopicFalsePositiveCount</c>, which counts off-topic queries that
/// returned anything at all. Every count is recorded beside the query count it is out of, so a reader
/// can see whether the corpus contributed any query of that category at all.
/// <para>
/// <c>UnansweredPositiveQueryCount</c> counts positive queries that came back with none of their
/// labelled relevant chunks. It is the measure <c>ChunkRecallAt5</c> was standing in for and is
/// strictly stronger for the question that matters: recall below 1.000 can mean either that an answer
/// was lost or that one of several chunks carrying the same answer was, and only this number tells
/// those apart.
/// </para>
/// <para>
/// <c>IncidentShapedPositiveConfirmedCount</c> counts incident-shaped positive queries, the ones whose
/// English source text is its signal's service, error type and message, that returned at least one of
/// their labelled relevant chunks banded <c>medium</c> or better. It is the measure that the band still
/// confirms a real answer once it is decided against the fault rather than against the query.
/// </para>
/// </summary>
public sealed record MemorySearchRelevanceJudgeBenchmarkLanguage(
    string Language,
    double ChunkRecallAt5,
    int PositiveQueryCount,
    int UnansweredPositiveQueryCount,
    int OffTopicQueryCount,
    int OffTopicFalsePositiveCount,
    int HardNegativeQueryCount,
    int HardNegativeReturnedCount,
    int HardNegativeConfirmedCount,
    int IncidentShapedPositiveQueryCount,
    int IncidentShapedPositiveConfirmedCount,
    IReadOnlyDictionary<string, int> BandCounts);
