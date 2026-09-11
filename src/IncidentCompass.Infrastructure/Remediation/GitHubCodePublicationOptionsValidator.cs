using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Remediation;

internal sealed class GitHubCodePublicationOptionsValidator
    : IValidateOptions<GitHubCodePublicationOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubCodePublicationOptions options)
    {
        if (options.TimeoutSeconds is < 1 or > 60)
        {
            return ValidateOptionsResult.Fail(
                "GitHub code publication TimeoutSeconds must be between 1 and 60.");
        }

        // Blank is not a bad value, it is the absence of one. A host that has not configured code
        // publication leaves this unset, and an unset value passed through a compose file arrives as
        // an empty string rather than as no key at all, so "not null" is not the same question as
        // "an operator said something here". Refusing blank would stop every deployment that never
        // asked for branch pushes, which is the opposite of what an unset base branch means. The
        // guarantee this validator exists for is unchanged and is kept one level up: blank leaves
        // GitHubCodePublicationOptions.IsConfigured false, every call refuses at dispatch with a
        // binding code, and CodePublicationConfigurationStartupValidator still refuses to start a
        // host whose configuration enables branch_push while the gateway reports no usable binding.
        if (string.IsNullOrWhiteSpace(options.BaseBranch))
        {
            return ValidateOptionsResult.Success;
        }

        if (!GitReferenceName.IsValid(options.BaseBranch))
        {
            return ValidateOptionsResult.Fail("GitHub code publication BaseBranch is invalid.");
        }

        // A base branch inside the namespace this product creates branches in would let a push build
        // on something an earlier push created, which is a chain nobody approved.
        return options.BaseBranch.StartsWith(
                IncidentCompass.Application.Remediation.RemediationBranchName.Prefix,
                StringComparison.Ordinal)
            ? ValidateOptionsResult.Fail(
                "GitHub code publication BaseBranch must not be inside the branch namespace this product creates.")
            : ValidateOptionsResult.Success;
    }
}
