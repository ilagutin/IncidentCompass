using System.ComponentModel.DataAnnotations;
using IncidentCompass.Application.Core.Configuration;

namespace IncidentCompass.Application.Core.Embeddings;

public sealed class EmbeddingOptions
{
    public const string SectionName = "IncidentCompass:Embeddings";

    [RequiredNonBlank]
    public string Provider { get; init; } = "Mock";

    [Range(1, 4096)]
    public int MockDimensions { get; init; } = 16;

    [Range(1, int.MaxValue)]
    public int MaxInputCharacters { get; init; } = 8000;
}
