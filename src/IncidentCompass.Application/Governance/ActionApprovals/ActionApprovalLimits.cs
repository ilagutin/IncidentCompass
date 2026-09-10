namespace IncidentCompass.Application.Governance.ActionApprovals;

public static class ActionApprovalLimits
{
    public const int ApprovalContractVersion = 1;
    public const int MaximumPayloadBytes = 64 * 1024;
    public const int MaximumResultBytes = 64 * 1024;
    public const int MinimumEvidenceCount = 1;
    public const int MaximumEvidenceCount = 32;
    public const int MaximumSummaryBytes = 2 * 1024;
    public const int MaximumLogicalTargetCharacters = 128;
    public const int MaximumProposalKeyCharacters = 256;
    public const int MinimumTtlMinutes = 1;
    public const int MaximumTtlMinutes = 10_080;
    public const int DefaultListLimit = 50;
    public const int MaximumListLimit = 100;
    public const int MaximumRejectionReasonCharacters = 500;
}
