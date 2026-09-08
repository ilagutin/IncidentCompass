using IncidentCompass.Infrastructure.Configuration;

namespace IncidentCompass.UnitTests;

public sealed class OpenAiCompatibleEndpointPolicyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void IsValid_EnforcesTimeoutBoundaries(int timeoutSeconds, bool expected)
    {
        var valid = OpenAiCompatibleEndpointPolicy.IsValid(
            "test-api-key",
            "https://provider.example",
            "/v1/chat/completions",
            allowInsecureHttpForLoopback: false,
            timeoutSeconds,
            maxRetryAttempts: 2,
            retryBaseDelayMilliseconds: 200);

        Assert.Equal(expected, valid);
    }
}
