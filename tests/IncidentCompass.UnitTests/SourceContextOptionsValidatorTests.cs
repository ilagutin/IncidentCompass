using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

public sealed class SourceContextOptionsValidatorTests
{
    [Fact]
    public void Validate_AcceptsBoundedAbsoluteMapping()
    {
        var options = ValidOptions();

        var result = new SourceContextOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(0, 256, 262144, 21)]
    [InlineData(9, 256, 262144, 21)]
    [InlineData(8, 0, 262144, 21)]
    [InlineData(8, 256, 1024, 101)]
    public void Validate_RejectsOutOfRangeLimits(int frames, int candidates, int bytes, int lines)
    {
        var options = ValidOptions();
        options.MaxFrames = frames;
        options.MaxCandidateFiles = candidates;
        options.MaxSourceBytes = bytes;
        options.MaxExcerptLines = lines;

        var result = new SourceContextOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
    }

    /// <summary>
    /// An unset workspace root is the shipped state and means remediation is off, not that the host
    /// is misconfigured. A set one must be absolute: the directory copies of a checkout are written
    /// into is not a thing to resolve against whatever working directory a host happens to have.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("/srv/incidentcompass/workspaces", true)]
    [InlineData("C:/incidentcompass/workspaces", true)]
    [InlineData("workspaces", false)]
    [InlineData("./workspaces", false)]
    [InlineData("", false)]
    public void Validate_AcceptsAnUnsetWorkspaceRootAndRequiresASetOneToBeAbsolute(
        string? workspaceRoot,
        bool expected)
    {
        var options = ValidOptions();
        options.WorkspaceRoot = workspaceRoot;

        var result = new SourceContextOptionsValidator().Validate(null, options);

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public void Validate_RejectsDuplicateMappingAndNonAbsoluteBuildPrefix()
    {
        var options = ValidOptions();
        options.Roots = [options.Roots[0], options.Roots[0]];
        options.Roots[0].BuildPathPrefixes = ["relative/build"];

        var result = new SourceContextOptionsValidator().Validate(null, options);

        Assert.False(result.Succeeded);
    }

    private static SourceContextOptions ValidOptions() => new()
    {
        Roots =
        [
            new SourceContextRootOptions
            {
                ServiceName = "checkout",
                Release = "2026.08.28.1",
                RootPath = Path.GetPathRoot(Environment.CurrentDirectory)!,
                BuildPathPrefixes = [Path.GetPathRoot(Environment.CurrentDirectory)!]
            }
        ]
    };
}
