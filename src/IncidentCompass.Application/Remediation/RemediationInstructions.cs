namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The system message one remediation pass runs under.
/// </summary>
/// <remarks>
/// It is a backend constant rather than a <c>ref:</c> file like the orchestrator and role
/// instructions, and the difference is deliberate. Those files exist so an operator can change how
/// an investigation reasons. This text does not describe reasoning; it states the shape of the one
/// answer the backend will accept, and every rule in it is enforced afterwards by the parser, the
/// applier and the base check. An operator who edited it could only make the prompt disagree with
/// the code that judges the answer, which costs a refused turn and explains nothing. The answer
/// rules themselves live in <see cref="RemediationPromptBuilder"/>, beside the request and the
/// correction that repeat them, so there is one place where what is asked for is written down.
/// </remarks>
internal static class RemediationInstructions
{
    public const string Text =
        "You are a remediation assistant for IncidentCompass. You are given one grounded incident " +
        "report and the source excerpts the investigation actually read, and you answer with one " +
        "unified diff that addresses the fault those excerpts show. " +
        "You are not an agent: you cannot read files, run commands, run tests or ask questions, and " +
        "nothing you write is executed. Your answer is parsed by a backend that applies it to a " +
        "throwaway copy of the checkout and records the result for a human to review. " +
        "Change only what the evidence supports, and keep the change as small as the fault allows.";
}
