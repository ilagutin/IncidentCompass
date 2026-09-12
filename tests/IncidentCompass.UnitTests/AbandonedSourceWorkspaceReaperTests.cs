using IncidentCompass.Infrastructure.SourceContext;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The third retention operation, over a real directory tree.
/// </summary>
/// <remarks>
/// Nothing here waits for a clock. Age is expressed by writing a directory whose name states an old
/// instant and whose last write is set to an old instant, so every case is decided by values the
/// test chose rather than by which of two timers fires first. That is also why the operation dates a
/// workspace from its own name: a directory's creation time cannot be set back on Linux, so a test
/// that depended on it could not exist, and neither could a reaper that trusted it.
/// </remarks>
public sealed class AbandonedSourceWorkspaceReaperTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string temporaryRoot =
        Directory.CreateTempSubdirectory("ic-workspace-reap-").FullName;

    private string WorkspaceRoot => Path.Combine(temporaryRoot, "workspaces");

    [Fact]
    public async Task Reap_DeletesAWorkspaceOlderThanTheWindow()
    {
        var abandoned = CreateWorkspace(Now.AddHours(-7));

        var outcome = await ReapAsync();

        Assert.Equal(SourceWorkspaceReapCodes.Completed, outcome.Code);
        Assert.Equal(1, outcome.Reaped);
        Assert.Equal(0, outcome.Kept);
        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public async Task Reap_LeavesAWorkspaceYoungerThanTheWindow()
    {
        var fresh = CreateWorkspace(Now.AddHours(-1));

        var outcome = await ReapAsync();

        Assert.Equal(0, outcome.Reaped);
        Assert.Equal(1, outcome.Kept);
        Assert.True(Directory.Exists(fresh));
    }

    /// <summary>
    /// A workspace a pass is still filling. Its name is old enough to reap - the copy started before
    /// the window - but something is writing into it right now, and the second age says so. Either
    /// age looking recent keeps the directory, because deleting a tree a running pass is about to
    /// identify turns a working pass into a filesystem fault.
    /// </summary>
    [Fact]
    public async Task Reap_LeavesAWorkspaceThatIsStillBeingWrittenTo()
    {
        var inUse = CreateWorkspace(Now.AddHours(-9), lastWriteUtc: Now.AddMinutes(-1));

        var outcome = await ReapAsync();

        Assert.Equal(0, outcome.Reaped);
        Assert.Equal(1, outcome.Kept);
        Assert.True(Directory.Exists(inUse));
    }

    /// <summary>
    /// Something else under the workspace root. It carries the prefix but states no instant, so this
    /// run cannot date it and must not delete it: an unreadable name is not evidence of age.
    /// </summary>
    [Fact]
    public async Task Reap_LeavesADirectoryWhoseNameStatesNoInstant()
    {
        var undatable = Path.Combine(WorkspaceRoot, SourceWorkspaceDirectory.NamePrefix + "0123abcd");
        Directory.CreateDirectory(undatable);
        Directory.SetLastWriteTimeUtc(undatable, Now.AddDays(-30).UtcDateTime);

        var outcome = await ReapAsync();

        Assert.Equal(0, outcome.Reaped);
        Assert.Equal(1, outcome.Kept);
        Assert.True(Directory.Exists(undatable));
    }

    /// <summary>
    /// The prefix is the whole admission rule for what this run may touch. A sibling directory the
    /// operator put under the workspace root is not ours and is not even examined.
    /// </summary>
    [Fact]
    public async Task Reap_NeverTouchesADirectoryOutsideTheWorkspaceNaming()
    {
        var foreign = Path.Combine(WorkspaceRoot, "operator-notes");
        Directory.CreateDirectory(foreign);
        Directory.SetLastWriteTimeUtc(foreign, Now.AddDays(-30).UtcDateTime);

        var outcome = await ReapAsync();

        Assert.Equal(0, outcome.Examined);
        Assert.True(Directory.Exists(foreign));
    }

    [Fact]
    public async Task Reap_StopsAtItsConfiguredBudgetAndFinishesOnTheNextRun()
    {
        for (var index = 0; index < 5; index++)
        {
            CreateWorkspace(Now.AddHours(-8));
        }

        var first = await ReapAsync(maxDirectories: 2);
        var second = await ReapAsync(maxDirectories: 2);
        var third = await ReapAsync(maxDirectories: 2);

        Assert.Equal(2, first.Reaped);
        Assert.Equal(2, second.Reaped);
        Assert.Equal(1, third.Reaped);
        Assert.Empty(Directory.GetDirectories(WorkspaceRoot));
    }

    /// <summary>
    /// Running twice over the same root is running once and then finding nothing, not running once
    /// and then failing on what is already gone.
    /// </summary>
    [Fact]
    public async Task Reap_IsIdempotent()
    {
        CreateWorkspace(Now.AddHours(-8));

        var first = await ReapAsync();
        var second = await ReapAsync();

        Assert.Equal(1, first.Reaped);
        Assert.Equal(0, second.Reaped);
        Assert.Equal(0, second.Examined);
        Assert.Equal(0, second.Failed);
        Assert.Equal(SourceWorkspaceReapCodes.Completed, second.Code);
    }

    /// <summary>
    /// What a partial delete leaves behind: a directory that still carries the prefix and still
    /// states the same old instant, so the next run finishes what the killed one started.
    /// </summary>
    [Fact]
    public async Task Reap_FinishesAWorkspaceAPreviousRunOnlyPartlyDeleted()
    {
        var partial = CreateWorkspace(Now.AddHours(-8));
        Directory.CreateDirectory(Path.Combine(partial, "src"));
        File.WriteAllText(Path.Combine(partial, "src", "left-behind.cs"), "// leftover");
        Directory.SetLastWriteTimeUtc(partial, Now.AddHours(-8).UtcDateTime);

        var outcome = await ReapAsync();

        Assert.Equal(1, outcome.Reaped);
        Assert.False(Directory.Exists(partial));
    }

    /// <summary>
    /// The shipped state. No workspace root is configured, so nothing writes workspaces, and the run
    /// says exactly that instead of failing or inventing a location to sweep.
    /// </summary>
    [Fact]
    public async Task Reap_SaysSoOnAHostWithNoWorkspaceRootConfigured()
    {
        var outcome = await ReapAsync(workspaceRoot: null);

        Assert.Equal(SourceWorkspaceReapCodes.NotConfigured, outcome.Code);
        Assert.Equal(0, outcome.Examined);
        Assert.Equal(0, outcome.Reaped);
    }

    /// <summary>
    /// A configured root that nothing has written to yet. Materialization creates it on first use,
    /// so before a first pass this is normal rather than an error.
    /// </summary>
    [Fact]
    public async Task Reap_SaysSoWhenTheConfiguredRootDoesNotExistYet()
    {
        var outcome = await ReapAsync(workspaceRoot: Path.Combine(temporaryRoot, "never-written"));

        Assert.Equal(SourceWorkspaceReapCodes.RootAbsent, outcome.Code);
        Assert.Equal(0, outcome.Reaped);
    }

    /// <summary>
    /// The two halves have to agree: what materialization writes is what this run reads back. A name
    /// shape that drifted would make every real leftover undatable and therefore immortal.
    /// </summary>
    [Fact]
    public void CreatedWorkspaceNameCarriesAnInstantTheReaperCanRead()
    {
        Directory.CreateDirectory(WorkspaceRoot);
        var created = SourceWorkspaceDirectory.Create(WorkspaceRoot);

        Assert.True(SourceWorkspaceDirectory.TryReadCreatedAtUtc(
            Path.GetFileName(created), out var createdAtUtc));
        Assert.InRange(
            createdAtUtc,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    /// <summary>
    /// The shipped window and budget, and the fact that neither floor is zero. A zero-hour window
    /// would delete a workspace the moment it exists, which is what an empty or mistyped setting
    /// would otherwise become.
    /// </summary>
    [Fact]
    public void DefaultsKeepALeftoverForSixHoursAndBoundTheRunAtSixtyFour()
    {
        var options = new SourceWorkspaceRetentionOptions();

        Assert.Equal(6, options.RetentionHours);
        Assert.Equal(64, options.MaxDirectoriesPerRun);
        Assert.True(new SourceWorkspaceRetentionOptionsValidator()
            .Validate(name: null, options).Succeeded);
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(-1, 64)]
    [InlineData(169, 64)]
    [InlineData(6, 0)]
    [InlineData(6, 10_001)]
    public void ValidatorRejectsAWindowOrBudgetOutsideItsRange(int hours, int maxDirectories)
    {
        var result = new SourceWorkspaceRetentionOptionsValidator().Validate(
            name: null,
            new SourceWorkspaceRetentionOptions
            {
                RetentionHours = hours,
                MaxDirectoriesPerRun = maxDirectories
            });

        Assert.True(result.Failed);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string CreateWorkspace(DateTimeOffset createdAtUtc, DateTimeOffset? lastWriteUtc = null)
    {
        Directory.CreateDirectory(WorkspaceRoot);
        var path = SourceWorkspaceDirectory.Create(WorkspaceRoot, createdAtUtc);
        Directory.SetLastWriteTimeUtc(path, (lastWriteUtc ?? createdAtUtc).UtcDateTime);
        return path;
    }

    private Task<SourceWorkspaceReapOutcome> ReapAsync(
        int maxDirectories = 64,
        string? workspaceRoot = "")
    {
        var reaper = new AbandonedSourceWorkspaceReaper(
            Options.Create(new SourceContextOptions
            {
                WorkspaceRoot = workspaceRoot is "" ? WorkspaceRoot : workspaceRoot
            }),
            Options.Create(new SourceWorkspaceRetentionOptions
            {
                RetentionHours = 6,
                MaxDirectoriesPerRun = maxDirectories
            }),
            new FixedTimeProvider(Now),
            new RecordingLogger<AbandonedSourceWorkspaceReaper>());
        return reaper.ReapAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
