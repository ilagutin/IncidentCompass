using System.Text.Json.Nodes;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Redaction replaces a matching property's value with the redaction marker whatever its kind, so a
/// configured attribute key naming <c>retrievalConfidence</c> would not hide a secret: it would
/// replace the band the publication rule reads, turning every confirmed memory match into a value
/// that confirms nothing and making the strongest classification unreachable. That is a
/// configuration that defeats itself, and it is refused at load rather than discovered as a run of
/// inexplicable refusals.
/// </summary>
public sealed class ReservedRedactionAttributeKeyTests
{
    [Theory]
    [InlineData("retrievalConfidence")]
    [InlineData("RETRIEVALCONFIDENCE")]
    [InlineData("RetrievalConfidence")]
    public void Validate_AnAttributeKeyNamingTheRetrievalBand_IsRefused(string key)
    {
        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            RedactionSettingsLoadValidator.Validate(new RedactionSettings([key], [], [])));

        Assert.Contains("Redaction.AttributeKeys", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            MemoryRetrievalConfidence.PayloadPropertyName,
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The same key is refused in the user-identifier list, which pseudonymizes rather than redacts
    /// but replaces the value just as surely.
    /// </summary>
    [Fact]
    public void Validate_TheRetrievalBandInTheUserIdentifierList_IsRefused()
    {
        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            RedactionSettingsLoadValidator.Validate(
                new RedactionSettings([], [], [MemoryRetrievalConfidence.PayloadPropertyName])));

        Assert.Contains("Redaction.UserIdentifierAttributes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AnOrdinaryAttributeKey_StillLoads()
    {
        RedactionSettingsLoadValidator.Validate(
            new RedactionSettings(["customer.account.id"], [], ["user.id"]));
    }

    /// <summary>
    /// And no configuration this repository carries names the reserved key, so the rule refuses a
    /// mistake an operator could make rather than one that is already shipped.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfigurationPaths))]
    public void EveryRepositoryConfiguration_LeavesTheRetrievalBandUnredacted(string configurationPath)
    {
        var configuration = JsonNode.Parse(File.ReadAllText(configurationPath))!.AsObject();

        foreach (var listName in new[] { "AttributeKeys", "UserIdentifierAttributes" })
        {
            var keys = configuration["Redaction"]?[listName]?.AsArray()
                .Select(static key => key!.GetValue<string>()) ?? [];
            Assert.DoesNotContain(
                keys,
                key => string.Equals(
                    key,
                    MemoryRetrievalConfidence.PayloadPropertyName,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    public static TheoryData<string> ConfigurationPaths
    {
        get
        {
            var configurationPaths = TriageConfigurationFileLocator.FindAll();
            TriageConfigurationFileLocator.AssertDiscoveryCoversEveryKnownConfiguration(configurationPaths);
            var data = new TheoryData<string>();
            foreach (var configurationPath in configurationPaths)
            {
                data.Add(configurationPath);
            }

            return data;
        }
    }
}
