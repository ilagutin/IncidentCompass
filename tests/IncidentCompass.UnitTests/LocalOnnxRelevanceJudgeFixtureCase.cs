namespace IncidentCompass.UnitTests;

/// <summary>One pair the fixture generator encoded and scored, with the ids and the score it recorded.</summary>
internal sealed record LocalOnnxRelevanceJudgeFixtureCase(string Query, string Passage, long[] Ids, float Score);
