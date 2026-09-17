using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The model-facing encoder reaches nothing durable, hashed or compared. Every digest below is over
/// text holding Cyrillic and Polish characters, which is exactly where a stray change of encoding
/// would show, and every one is pinned to the value the code produced before
/// <see cref="ModelFacingJson"/> existed.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the literals were obtained.</b> Each was printed by a throwaway test run against the
/// unchanged code path, before any call site was touched, and copied here. They are literals rather
/// than values recomputed beside the assertion on purpose: a digest compared against itself would
/// stay green through any encoding change these are here to catch.
/// </para>
/// <para>
/// The canonical writer escapes every non-ASCII character to a six-character form, and that is
/// deliberate for this side of the code: a hash, a fingerprint and a stored payload are compared
/// across releases and processes, so their encoding is a contract rather than a rendering choice.
/// </para>
/// </remarks>
public sealed class ModelFacingJsonNonRegressionTests
{
    private const string PolishAndCyrillic = "Zażółć gęślą jaźń: превышено время ожидания";

    private const string CyrillicTitle = "Тайм-аут платежей";

    /// <summary>The canonical hash of <see cref="Payload"/>, and the content hash of an artifact over it.</summary>
    private const string CanonicalPayloadHash =
        "dbd0d2d8dafe43bbe585040f43cd55ef9834ac43e8fb69e84f3c6338395d1ede";

    private const string ConfigHash =
        "1909c080c59aff6dd251979bf5a53a10f394d6d7286dcb8ef36f03aed26a8d22";

    private const string SubstitutedEvidenceIdentity =
        "8e4616a8ff76cf3c8b412025a15b60eab205543cf4633f4f10298caf88976a6e";

    private const string WorkerToolFingerprint =
        "f3691a403903718ab2a4eb5bea9ec4b830c368316a76f82451f0072996484ba3";

    private const string DelegateFingerprint =
        "b1255b70c33d7e6f2d24009677dfdab358640eb07f0e6cfdee4645cdb0c6bf9a";

    [Fact]
    public void TheCanonicalFormOfANonAsciiPayload_IsUnchanged()
    {
        var canonical = CanonicalJsonSerializer.Canonicalize(Payload());

        Assert.Equal(CanonicalPayloadHash, CanonicalJsonSerializer.ComputeSha256Hex(canonical));
        Assert.Contains("\\u0422", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain(CyrillicTitle, canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfigurationHashOverNonAsciiText_IsUnchanged()
    {
        var hash = CanonicalJsonSerializer.ComputeSha256Hex(
            CanonicalJsonSerializer.Canonicalize(Payload()),
            CanonicalJsonSerializer.Canonicalize(new JsonObject
            {
                ["orchestrator"] = "Инструкция для оркестратора"
            }));

        Assert.Equal(ConfigHash, hash);
    }

    [Fact]
    public void AnArtifactContentHashOverANonAsciiPayload_IsUnchanged()
    {
        var artifact = RedactedToolArtifactFactory.Create(
            Job(),
            new ToolArtifactDraft(
                ArtifactKind.RetrievedItem,
                ArtifactDomainRef.Create("memory_item", "33333333-3333-3333-3333-333333333333"),
                Payload()),
            RedactionSettings.Default,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(CanonicalPayloadHash, artifact.ContentHash);
        Assert.Contains("\\u0422", artifact.RedactedPayload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEvidenceResultIdentityOverNonAsciiText_IsUnchanged()
    {
        var identity = EvidenceResultIdentity.Compute(
            CanonicalJsonSerializer.ToElement(Payload()),
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(CanonicalPayloadHash, identity);
    }

    [Fact]
    public void AnEvidenceResultIdentityThatSubstitutesAnArtifactId_IsUnchanged()
    {
        var identity = EvidenceResultIdentity.Compute(
            CanonicalJsonSerializer.ToElement(new JsonObject
            {
                ["note"] = "artifact:44444444-4444-4444-4444-444444444444 Тайм-аут",
                ["quote"] = "Zażółć"
            }),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["44444444-4444-4444-4444-444444444444"] = "seed-identity"
            });

        Assert.Equal(SubstitutedEvidenceIdentity, identity);
    }

    [Fact]
    public void AnEquivalentCallFingerprintOverNonAsciiArguments_IsUnchanged()
    {
        var workerTool = EquivalentCallFingerprint.ForWorkerTool(
            "memory_search",
            CanonicalJsonSerializer.ToElement(new JsonObject { ["query"] = CyrillicTitle }));
        var delegated = EquivalentCallFingerprint.ForDelegate("analysis", "Проверь причину сбоя");

        Assert.Equal(WorkerToolFingerprint, workerTool.Key);
        Assert.Equal(DelegateFingerprint, delegated.Key);
    }

    private static JsonObject Payload() => new()
    {
        ["title"] = CyrillicTitle,
        ["quote"] = PolishAndCyrillic,
        ["score"] = 0.5
    };

    private static TriageJob Job() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        TriageJobStatus.Processing,
        1,
        "worker",
        DateTimeOffset.UnixEpoch,
        null,
        null,
        null,
        "config-hash",
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);
}
