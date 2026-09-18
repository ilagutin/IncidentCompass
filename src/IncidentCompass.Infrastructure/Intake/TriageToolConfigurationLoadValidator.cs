using System.Globalization;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Domain.Incidents.Actions;
using static IncidentCompass.Infrastructure.Intake.TriageConfigurationValidationGuards;

namespace IncidentCompass.Infrastructure.Intake;

internal sealed class TriageToolConfigurationLoadValidator(IAgentToolRegistry toolRegistry)
{
    private static readonly HashSet<string> ToolKinds = new(["internal", "external_action"], StringComparer.Ordinal);
    private static readonly HashSet<string> ActionModes = new(["live", "dry_run", "disabled"], StringComparer.Ordinal);
    private const string MemorySearchToolName = "memory_search";

    public void Validate(
        IReadOnlyDictionary<string, TriageRouteSettings> routes,
        IReadOnlyDictionary<string, TriageToolSettings> tools,
        TriageActionSettings actions)
    {
        foreach (var (toolName, tool) in tools)
        {
            ValidateTool(routes, toolName, tool);
        }

        ValidateActions(tools, actions);
    }

    private void ValidateTool(
        IReadOnlyDictionary<string, TriageRouteSettings> routes,
        string toolName,
        TriageToolSettings tool)
    {
        RequireKey(toolName, "Tools");
        if (!AgentToolIdentity.IsValid(toolName))
        {
            throw Invalid("Tools." + toolName, toolName, "a safe case-sensitive backend tool id");
        }

        RequireKnown("Tools." + toolName + ".Kind", tool.Kind, ToolKinds);
        if (!toolRegistry.TryGet(toolName, out var descriptor))
        {
            throw Invalid("Tools." + toolName, toolName, "an exactly registered backend tool id");
        }

        var expectedCapability = string.Equals(tool.Kind, "external_action", StringComparison.Ordinal)
            ? AgentToolCapability.ExternalAction
            : AgentToolCapability.ImmediateRead;
        if (descriptor.Capability != expectedCapability)
        {
            throw Invalid("Tools." + toolName + ".Kind", tool.Kind, "the registered backend capability");
        }

        ValidateTimeout(toolName, tool);

        if (expectedCapability == AgentToolCapability.ExternalAction)
        {
            ValidateExternalTool(toolName, tool, descriptor);
            return;
        }

        ValidateImmediateTool(routes, toolName, tool);
    }

    /// <summary>
    /// The per-tool execution limit is valid for both kinds, so it is checked once here rather than
    /// in either kind's own rules, which each reject the other kind's fields.
    /// </summary>
    private static void ValidateTimeout(string toolName, TriageToolSettings tool)
    {
        if (tool.TimeoutSeconds is { } seconds &&
            seconds is < TriageToolSettings.MinimumTimeoutSeconds or > TriageToolSettings.MaximumTimeoutSeconds)
        {
            throw Invalid(
                "Tools." + toolName + ".TimeoutSeconds",
                seconds.ToString(CultureInfo.InvariantCulture),
                "an integer from 1 through 3600 when set");
        }
    }

    private static void ValidateImmediateTool(
        IReadOnlyDictionary<string, TriageRouteSettings> routes,
        string toolName,
        TriageToolSettings tool)
    {
        if (tool.Category is not null || tool.LogicalTargetId is not null || tool.Mode is not null)
        {
            throw Invalid("Tools." + toolName, toolName, "no external-action category, target or mode fields");
        }

        if (string.Equals(toolName, MemorySearchToolName, StringComparison.Ordinal))
        {
            ValidateMemorySearchTool(routes, toolName, tool);
            return;
        }

        if (HasMemorySearchRetrievalSettings(tool))
        {
            throw Invalid("Tools." + toolName, toolName, "no memory_search retrieval settings");
        }

        if (!string.IsNullOrWhiteSpace(tool.EmbeddingRouteId))
        {
            RequireEmbeddingRoute(routes, tool.EmbeddingRouteId, "Tools." + toolName + ".EmbeddingRouteId");
        }
    }

    private static void ValidateMemorySearchTool(
        IReadOnlyDictionary<string, TriageRouteSettings> routes,
        string toolName,
        TriageToolSettings tool)
    {
        RequireNonBlank("Tools." + toolName + ".EmbeddingRouteId", tool.EmbeddingRouteId ?? string.Empty);
        RequireEmbeddingRoute(routes, tool.EmbeddingRouteId!, "Tools." + toolName + ".EmbeddingRouteId");
        if (tool.TopK is <= 0)
        {
            throw Invalid("Tools." + toolName + ".TopK", tool.TopK.Value.ToString(CultureInfo.InvariantCulture), "a positive integer when set");
        }

        if (tool.MinScore is < -1 or > 1)
        {
            throw Invalid("Tools." + toolName + ".MinScore", tool.MinScore.Value.ToString(CultureInfo.InvariantCulture), "a score between -1 and 1 when set");
        }

        if (tool.VectorOnlyFallback is { } vectorOnlyFallback)
        {
            RequireKnown(
                "Tools." + toolName + "." + MemorySearchVectorOnlyFallbackSetting.SettingName,
                vectorOnlyFallback,
                MemorySearchVectorOnlyFallbackSetting.KnownValues);
        }

        ValidateRelevanceJudge(toolName, tool);
    }

    /// <summary>
    /// The three relevance-judge keys. Each score is bounded on its own, and the pair is then checked
    /// against itself: a floor above the confirm score would leave a band that can never be reached,
    /// which is a configuration mistake rather than a strict policy.
    /// </summary>
    private static void ValidateRelevanceJudge(string toolName, TriageToolSettings tool)
    {
        if (tool.RelevanceJudge is { } relevanceJudge)
        {
            RequireKnown(
                "Tools." + toolName + "." + MemoryRelevanceJudgeSetting.ModeSettingName,
                relevanceJudge,
                MemoryRelevanceJudgeSetting.KnownModeValues);
        }

        RequireJudgeScoreInRange(
            toolName, MemoryRelevanceJudgeSetting.ConfirmScoreSettingName, tool.RelevanceConfirmScore);
        RequireJudgeScoreInRange(
            toolName, MemoryRelevanceJudgeSetting.FloorScoreSettingName, tool.RelevanceFloorScore);

        var confirmScore = tool.RelevanceConfirmScore ?? MemoryRelevanceJudgeSetting.DefaultConfirmScore;
        var floorScore = tool.RelevanceFloorScore ?? MemoryRelevanceJudgeSetting.DefaultFloorScore;
        if (floorScore > confirmScore)
        {
            throw Invalid(
                "Tools." + toolName + "." + MemoryRelevanceJudgeSetting.FloorScoreSettingName,
                floorScore.ToString(CultureInfo.InvariantCulture),
                "a score at or below Tools." + toolName + "." +
                    MemoryRelevanceJudgeSetting.ConfirmScoreSettingName);
        }
    }

    private static void RequireJudgeScoreInRange(string toolName, string settingName, double? value)
    {
        if (value is { } score && !MemoryRelevanceJudgeSetting.IsScoreInRange(score))
        {
            throw Invalid(
                "Tools." + toolName + "." + settingName,
                score.ToString(CultureInfo.InvariantCulture),
                "a score between " + MemoryRelevanceJudgeSetting.MinimumScore.ToString(CultureInfo.InvariantCulture) +
                    " and " + MemoryRelevanceJudgeSetting.MaximumScore.ToString(CultureInfo.InvariantCulture) +
                    " when set");
        }
    }

    /// <summary>Every retrieval setting that belongs to <c>memory_search</c> and to no other tool.</summary>
    private static bool HasMemorySearchRetrievalSettings(TriageToolSettings tool) =>
        tool.VectorOnlyFallback is not null ||
        tool.RelevanceJudge is not null ||
        tool.RelevanceConfirmScore is not null ||
        tool.RelevanceFloorScore is not null;

    private static void ValidateExternalTool(
        string toolName,
        TriageToolSettings tool,
        AgentToolDescriptor descriptor)
    {
        if (!TryParseCategory(tool.Category, out var category) || descriptor.Category != category)
        {
            throw Invalid("Tools." + toolName + ".Category", tool.Category ?? "", "the registered closed action category");
        }

        RequireNonBlank("Tools." + toolName + ".LogicalTargetId", tool.LogicalTargetId ?? "");
        if (tool.LogicalTargetId!.Length > ActionApprovalLimits.MaximumLogicalTargetCharacters ||
            !string.Equals(tool.LogicalTargetId, descriptor.LogicalTargetId, StringComparison.Ordinal))
        {
            throw Invalid("Tools." + toolName + ".LogicalTargetId", tool.LogicalTargetId, "the registered safe logical target id");
        }

        if (tool.EmbeddingRouteId is not null || tool.TopK is not null || tool.MinScore is not null ||
            HasMemorySearchRetrievalSettings(tool))
        {
            throw Invalid("Tools." + toolName, toolName, "no immediate-read settings");
        }

        if (tool.Mode is not null)
        {
            RequireKnown("Tools." + toolName + ".Mode", tool.Mode, ActionModes);
        }
    }

    private static void ValidateActions(
        IReadOnlyDictionary<string, TriageToolSettings> tools,
        TriageActionSettings actions)
    {
        RequireKnown("Actions.DefaultMode", actions.DefaultMode, ActionModes);
        if (actions.ApprovalTtlMinutes is < ActionApprovalLimits.MinimumTtlMinutes or > ActionApprovalLimits.MaximumTtlMinutes)
        {
            throw Invalid("Actions.ApprovalTtlMinutes", actions.ApprovalTtlMinutes.ToString(CultureInfo.InvariantCulture), "an integer from 1 through 10080");
        }

        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolName in actions.AllowedTools)
        {
            RequireNonBlank("Actions.AllowedTools", toolName);
            if (!unique.Add(toolName))
            {
                throw Invalid("Actions.AllowedTools", toolName, "unique external action tool ids");
            }

            if (!tools.TryGetValue(toolName, out var tool) ||
                !string.Equals(tool.Kind, "external_action", StringComparison.Ordinal))
            {
                throw Invalid("Actions.AllowedTools", toolName, "a configured registered external action tool id");
            }

            if (tool.Mode is not null && ModeRank(tool.Mode) < ModeRank(actions.DefaultMode))
            {
                throw Invalid("Tools." + toolName + ".Mode", tool.Mode, "the global mode or a more restrictive mode");
            }
        }

        if (!NotificationRouteSelector.TryValidateConfiguration(
                tools, actions, out var field, out var value, out var expectation))
        {
            throw Invalid(field, value, expectation);
        }
    }

    private static int ModeRank(string mode) => mode switch
    {
        "live" => 0,
        "dry_run" => 1,
        "disabled" => 2,
        _ => int.MaxValue
    };

    private static bool TryParseCategory(string? value, out ActionCategory category)
    {
        category = value switch
        {
            "notification" => ActionCategory.Notification,
            "ticket_create" => ActionCategory.TicketCreate,
            "ticket_update" => ActionCategory.TicketUpdate,
            "code_write" => ActionCategory.CodeWrite,
            "branch_push" => ActionCategory.BranchPush,
            "pr_create" => ActionCategory.PrCreate,
            _ => default
        };
        return value is "notification" or "ticket_create" or "ticket_update" or
            "code_write" or "branch_push" or "pr_create";
    }
}
