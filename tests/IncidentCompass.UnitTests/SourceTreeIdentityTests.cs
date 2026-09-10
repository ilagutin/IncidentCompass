using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The identity is a claim about a base a diff applies to, so these assert the properties that claim
/// rests on: it depends on the pairing of path and content, not on the order entries arrived in, and
/// it separates anything a patch would separate.
/// </summary>
public sealed class SourceTreeIdentityTests
{
    [Fact]
    public void Compute_IgnoresEntryOrder()
    {
        SourceTreeEntry[] ordered =
        [
            new("a.cs", "00"),
            new("b.cs", "11"),
            new("nested/c.cs", "22")
        ];
        SourceTreeEntry[] shuffled = [ordered[2], ordered[0], ordered[1]];

        Assert.Equal(SourceTreeIdentity.Compute(ordered), SourceTreeIdentity.Compute(shuffled));
    }

    [Fact]
    public void Compute_BindsEachPathToItsOwnContent()
    {
        SourceTreeEntry[] paired = [new("a.cs", "00"), new("b.cs", "11")];
        SourceTreeEntry[] swapped = [new("a.cs", "11"), new("b.cs", "00")];

        Assert.NotEqual(SourceTreeIdentity.Compute(paired), SourceTreeIdentity.Compute(swapped));
    }

    [Fact]
    public void Compute_LengthFramingSeparatesPathFromContent()
    {
        // Without a length prefix on every field these two would feed the hash the same byte run.
        Assert.NotEqual(
            SourceTreeIdentity.Compute([new("ab", "cd")]),
            SourceTreeIdentity.Compute([new("a", "bcd")]));
    }

    [Fact]
    public void Compute_DoesNotFoldCase()
    {
        Assert.NotEqual(
            SourceTreeIdentity.Compute([new("Checkout.cs", "00")]),
            SourceTreeIdentity.Compute([new("checkout.cs", "00")]));
    }

    [Fact]
    public void Compute_SeparatesAddedAndRemovedFiles()
    {
        Assert.NotEqual(
            SourceTreeIdentity.Compute([new("a.cs", "00")]),
            SourceTreeIdentity.Compute([new("a.cs", "00"), new("b.cs", "00")]));
    }

    [Fact]
    public void Compute_ReturnsLowerHexSha256ForAnEmptyTree()
    {
        var identity = SourceTreeIdentity.Compute([]);

        Assert.Matches("^[0-9a-f]{64}$", identity);
        Assert.Equal(identity, SourceTreeIdentity.Compute([]));
    }
}
