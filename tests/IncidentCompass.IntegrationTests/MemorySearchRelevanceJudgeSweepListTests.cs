using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The sweep lists, checked without the model. The benchmark that uses them needs a real 544 MiB
/// download and a Postgres container, so a list that had drifted, lost its fine steps, stopped covering
/// the shipped value or formed a pair the loader refuses would only be found by the person running it,
/// and only after the run. Nothing here scores anything; it asserts the shape of two arrays of numbers
/// and feeds every pair to the rule that decides whether a configuration is representable.
/// </summary>
public sealed class MemorySearchRelevanceJudgeSweepListTests
{
    [Fact]
    public void FloorScoreCandidates_AreStrictlyIncreasingAndCoverBothSidesOfTheAnswer()
    {
        var values = MemorySearchRelevanceJudgeBenchmarkTests.FloorScoreCandidates;

        AssertStrictlyIncreasing(values);
        Assert.Equal(97, values.Count);
        Assert.Equal(-9.0, values[0]);
        Assert.Equal(1.5, values[^1]);
    }

    /// <summary>
    /// The working range is where the answer lives and the only place the step size matters. A window
    /// as narrow as a third of the one measured offline still contains several samples at 0.025, so the
    /// chosen floor cannot fall between two of them.
    /// </summary>
    [Fact]
    public void FloorScoreCandidates_StepFinelyThroughTheWorkingRange()
    {
        var working = MemorySearchRelevanceJudgeBenchmarkTests.FloorScoreCandidates
            .Where(static value => value is >= -2.0 and <= 0.0)
            .ToArray();

        Assert.Equal(81, working.Length);
        for (var index = 1; index < working.Length; index++)
        {
            Assert.Equal(0.025, working[index] - working[index - 1], 6);
        }
    }

    [Fact]
    public void ConfirmScoreCandidates_AreStrictlyIncreasingAndStepFinelyAcrossTheBandGap()
    {
        var values = MemorySearchRelevanceJudgeBenchmarkTests.ConfirmScoreCandidates;

        AssertStrictlyIncreasing(values);
        Assert.Equal(105, values.Count);
        Assert.Equal(-2.0, values[0]);
        Assert.Equal(8.0, values[^1]);
        var fine = values.Where(static value => value is >= -2.0 and <= 3.0).ToArray();
        Assert.Equal(101, fine.Length);
        for (var index = 1; index < fine.Length; index++)
        {
            Assert.Equal(0.05, fine[index] - fine[index - 1], 6);
        }
    }

    /// <summary>
    /// The shipped default has to be one of the measured points, or the curve says nothing about the
    /// value actually in use and the acceptance bar cannot be read against it.
    /// </summary>
    [Fact]
    public void BothSweepsMeasureTheShippedDefault()
    {
        Assert.Contains(MemoryRelevanceJudgeSetting.DefaultFloorScore, MeasuredFloorScores());
        Assert.Contains(MemoryRelevanceJudgeSetting.DefaultConfirmScore, MeasuredConfirmScores());
    }

    /// <summary>
    /// Every measured floor value has to form a configuration that can exist with the confirm score it
    /// is swept against. This is the check that was missing when a floor of 1.25 was swept against a
    /// confirm score of 1.15: the run crashed inside the tool before writing anything, and the whole
    /// measurement was lost.
    /// </summary>
    [Fact]
    public void EveryMeasuredFloorFormsARepresentablePairWithTheHeldConfirmScore()
    {
        Assert.All(
            MeasuredFloorScores(),
            floorScore => AssertRepresentable(
                MemoryRelevanceJudgeSetting.DefaultConfirmScore, floorScore));
    }

    /// <summary>
    /// The mirror of the same rule. The confirm sweep holds the floor at its shipped default, and a
    /// confirm score below that floor is the same refused pair the other way round. It had the identical
    /// defect and had simply not run yet.
    /// </summary>
    [Fact]
    public void EveryMeasuredConfirmScoreFormsARepresentablePairWithTheHeldFloor()
    {
        Assert.All(
            MeasuredConfirmScores(),
            confirmScore => AssertRepresentable(
                confirmScore, MemoryRelevanceJudgeSetting.DefaultFloorScore));
    }

    /// <summary>
    /// The exclusion is derived from the held threshold, not hard-coded, so a future default keeps a
    /// representable list. Both directions are exercised at defaults far from the shipped ones.
    /// </summary>
    [Theory]
    [InlineData(-3.0)]
    [InlineData(0.0)]
    [InlineData(1.15)]
    [InlineData(6.0)]
    public void TheDerivedListsStayRepresentableAtAnyDefault(double held)
    {
        var floors = MemorySearchRelevanceJudgeBenchmarkTests.FloorScoresFor(held);
        var confirms = MemorySearchRelevanceJudgeBenchmarkTests.ConfirmScoresFor(held);

        Assert.All(floors, floorScore => AssertRepresentable(held, floorScore));
        Assert.All(confirms, confirmScore => AssertRepresentable(confirmScore, held));
        Assert.NotEmpty(floors);
        Assert.NotEmpty(confirms);
    }

    /// <summary>
    /// Trimming removes only the values it has to. The dense working range survives whole at the shipped
    /// confirm score, so the curve where the answer lives is unchanged by the bound.
    /// </summary>
    [Fact]
    public void TrimmingKeepsTheDenseWorkingRangeAndOnlyDropsTheTail()
    {
        var measured = MeasuredFloorScores();

        Assert.Equal(95, measured.Count);
        Assert.Equal(1.0, measured[^1]);
        Assert.Equal(
            81,
            measured.Count(static value => value is >= -2.0 and <= 0.0));
        Assert.Equal(
            70,
            MeasuredConfirmScores().Count);
        Assert.Equal(MemoryRelevanceJudgeSetting.DefaultFloorScore, MeasuredConfirmScores()[0]);
    }

    /// <summary>
    /// Every swept value has to be one the loader would accept on its own, or the sweep would measure a
    /// setting no operator could put in a configuration file.
    /// </summary>
    [Fact]
    public void EverySweptValueIsInsideTheConfigurableRange()
    {
        Assert.All(
            MemorySearchRelevanceJudgeBenchmarkTests.FloorScoreCandidates,
            static value => Assert.True(MemoryRelevanceJudgeSetting.IsScoreInRange(value)));
        Assert.All(
            MemorySearchRelevanceJudgeBenchmarkTests.ConfirmScoreCandidates,
            static value => Assert.True(MemoryRelevanceJudgeSetting.IsScoreInRange(value)));
    }

    private static IReadOnlyList<double> MeasuredFloorScores() =>
        MemorySearchRelevanceJudgeBenchmarkTests.FloorScoresFor(
            MemoryRelevanceJudgeSetting.DefaultConfirmScore);

    private static IReadOnlyList<double> MeasuredConfirmScores() =>
        MemorySearchRelevanceJudgeBenchmarkTests.ConfirmScoresFor(
            MemoryRelevanceJudgeSetting.DefaultFloorScore);

    /// <summary>
    /// Runs the production rule rather than repeating its comparison, so this test cannot drift from it.
    /// <see cref="MemoryRelevanceJudgeSetting.Resolve" /> is the one that threw during the failed sweep,
    /// and a unit test already pins it against the loader's own refusal of the same pair.
    /// </summary>
    private static void AssertRepresentable(double confirmScore, double floorScore) =>
        MemoryRelevanceJudgeSetting.Resolve(new TriageToolSettings("internal", "memory-embed", 5, 0.25)
        {
            RelevanceJudge = MemoryRelevanceJudgeSetting.On,
            RelevanceConfirmScore = confirmScore,
            RelevanceFloorScore = floorScore
        });

    private static void AssertStrictlyIncreasing(IReadOnlyList<double> values)
    {
        for (var index = 1; index < values.Count; index++)
        {
            Assert.True(
                values[index] > values[index - 1],
                $"Sweep value {values[index]} does not follow {values[index - 1]}.");
        }
    }
}
