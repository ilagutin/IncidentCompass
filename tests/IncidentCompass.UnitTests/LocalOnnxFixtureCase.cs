using IncidentCompass.Application.Core.Embeddings;

namespace IncidentCompass.UnitTests;

/// <summary>One input the fixture generator encoded, with the mapped ids it recorded.</summary>
internal sealed record LocalOnnxFixtureCase(EmbeddingInputKind Kind, string Input, long[] Ids);
