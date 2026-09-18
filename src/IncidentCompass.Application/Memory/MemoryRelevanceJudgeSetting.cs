using System.Globalization;
using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Application.Memory;

/// <summary>
/// The configured spelling of the three relevance-judge keys of <c>Tools.memory_search</c>, in one
/// place so no other file repeats the literals: <c>RelevanceJudge</c>, <c>RelevanceConfirmScore</c>
/// and <c>RelevanceFloorScore</c>. All three are optional, so a configuration that sets none of them
/// keeps its content and therefore its configuration hash.
/// </summary>
internal static class MemoryRelevanceJudgeSetting
{
    public const string ModeSettingName = "RelevanceJudge";

    public const string ConfirmScoreSettingName = "RelevanceConfirmScore";

    public const string FloorScoreSettingName = "RelevanceFloorScore";

    public const string Off = "off";

    public const string On = "on";

    public static readonly IReadOnlyList<string> KnownModeValues = [Off, On];

    public const MemoryRelevanceJudgeMode DefaultMode = MemoryRelevanceJudgeMode.On;

    /// <summary>
    /// The default confirm threshold, measured for the pinned cross-encoder on the retrieval
    /// benchmark corpus: every positive query's answer scored above it, and every off-topic passage
    /// scored well below it.
    /// </summary>
    public const double DefaultConfirmScore = 1.15;

    /// <summary>
    /// The default floor. A candidate the judge scores below it is dropped, not banded.
    /// <para>
    /// It does not sit in a comfortable gap, because on this corpus there is no gap. Measured through
    /// the product, the strongest scoring off-topic pair is
    /// <c>off-topic-notification-template-render</c> against the notification runbook at -0.527, a
    /// query about the same service and a different failure, and the weakest labelled relevant chunk
    /// is the second chunk of <c>stock-reservation-query-variant</c> at -0.872. The relevant chunk
    /// scores below the off-topic one, so the window is -0.344 wide: no floor exists that returns
    /// every labelled chunk and also leaves every off-topic query empty. Polish and Russian have the
    /// same shape with wider negative windows, and no labelled chunk is missing from the candidate
    /// sets, so this is the judge's ordering rather than a retrieval shortfall.
    /// </para>
    /// <para>
    /// This value takes the first of those two and gives up the second, because an off-topic query
    /// returning nothing is the stated product requirement, while the labelled chunk lost here is the
    /// redundant second chunk of a query whose first chunk is still returned: the answer survives and
    /// only a duplicate of it does not. At -0.25 no English positive query comes back empty, off-topic
    /// false positives are 0 of 6 in all three languages, and no hard negative is confirmed. The
    /// margin to the strongest off-topic pair is 0.277, and English recall is flat from -0.5 through
    /// 0.25 before falling at 0.5, so the value sits inside a plateau rather than on its edge.
    /// </para>
    /// <para>
    /// Every number above comes from the opt-in benchmark leg that runs the real <c>memory_search</c>,
    /// and from nowhere else. An offline sweep of the same graph put those two bounding pairs 0.167
    /// apart in the opposite order: the int8 model scores differently enough between ONNX runtime
    /// builds to reorder a pair by more than the entire window, so a threshold measured anywhere but
    /// through the product does not transfer to it.
    /// </para>
    /// </summary>
    public const double DefaultFloorScore = -0.25;

    /// <summary>
    /// The bound both scores must sit inside. It cannot be the pinned model's measured range, because
    /// the port's contract is that the scale belongs to the adapter's model and another cross-encoder
    /// may use a wider one. This is roughly six times the widest score the pinned judge produced on
    /// the benchmark corpus, which leaves a different model room while still refusing the mistakes
    /// worth refusing: a percentage, a similarity meant for <c>MinScore</c>, or a misplaced decimal
    /// point.
    /// </summary>
    public const double MinimumScore = -50;

    /// <summary>The upper end of the bound described on <see cref="MinimumScore" />.</summary>
    public const double MaximumScore = 50;

    public static bool TryParseMode(string? value, out MemoryRelevanceJudgeMode mode)
    {
        switch (value)
        {
            case Off:
                mode = MemoryRelevanceJudgeMode.Off;
                return true;
            case On:
                mode = MemoryRelevanceJudgeMode.On;
                return true;
            default:
                mode = DefaultMode;
                return false;
        }
    }

    public static bool IsScoreInRange(double value) => value is >= MinimumScore and <= MaximumScore;

    /// <summary>
    /// Configuration load already refuses an unknown mode, a score outside the bound and a floor above
    /// the confirm score, so reaching a throw here means a snapshot was written by a loader that did
    /// not check them; failing closed is better than silently guessing.
    /// </summary>
    public static MemoryRelevanceJudgeSettings Resolve(TriageToolSettings tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var mode = DefaultMode;
        if (tool.RelevanceJudge is { } configuredMode && !TryParseMode(configuredMode, out mode))
        {
            throw new InvalidOperationException(
                "Tools.memory_search." + ModeSettingName + " is '" + configuredMode +
                "'; the accepted values are " + string.Join(", ", KnownModeValues) + ".");
        }

        var confirmScore = tool.RelevanceConfirmScore ?? DefaultConfirmScore;
        var floorScore = tool.RelevanceFloorScore ?? DefaultFloorScore;
        RequireInRange(ConfirmScoreSettingName, confirmScore);
        RequireInRange(FloorScoreSettingName, floorScore);
        if (floorScore > confirmScore)
        {
            throw new InvalidOperationException(
                "Tools.memory_search." + FloorScoreSettingName + " is " + Format(floorScore) +
                ", above Tools.memory_search." + ConfirmScoreSettingName + " " + Format(confirmScore) + ".");
        }

        return new MemoryRelevanceJudgeSettings(mode, confirmScore, floorScore);
    }

    private static void RequireInRange(string settingName, double value)
    {
        if (!IsScoreInRange(value))
        {
            throw new InvalidOperationException(
                "Tools.memory_search." + settingName + " is " + Format(value) + "; it must be from " +
                Format(MinimumScore) + " through " + Format(MaximumScore) + ".");
        }
    }

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
