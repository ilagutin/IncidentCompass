using IncidentCompass.Application.Core.Composition;
using IncidentCompass.Application.Core.Security;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Notifications.Telegram;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Worker;

public static class Setup
{
    public static IServiceCollection AddWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<WorkerOptions>()
            .Bind(configuration.GetSection(WorkerOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<WorkerOptions>,
            WorkerOptionsValidator>());
        services
            .AddOptions<ActionDispatchOptions>()
            .Bind(configuration.GetSection(ActionDispatchOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ActionDispatchOptions>,
            ActionDispatchOptionsValidator>());
        services
            .AddOptions<PostReportActionEvaluationOptions>()
            .Bind(configuration.GetSection(PostReportActionEvaluationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<PostReportActionEvaluationOptions>,
            PostReportActionEvaluationOptionsValidator>());
        services
            .AddOptions<RetentionScheduleOptions>()
            .Bind(configuration.GetSection(RetentionScheduleOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<RetentionScheduleOptions>,
            RetentionScheduleOptionsValidator>());
        services
            .AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<TelegramOptions>,
            TelegramOptionsValidator>());

        services.AddSingleton<IPostReportActionWorkflow, TelegramNotificationWorkflow>();
        services.AddSingleton<IPostReportActionWorkflow, TicketCreatePostReportActionWorkflow>();
        services.AddSingleton<IPostReportActionWorkflow, TicketUpdatePostReportActionWorkflow>();

        // The remediation pass is scheduled here and nowhere else: one post-report workflow on the
        // evaluation loop that already owns fenced claims, attempt caps, dead-lettering and a lease
        // renewer long calls survive. It runs only where investigation runs, because that is the one
        // host composed with the model gateway and the workspace adapter it needs.
        services.AddSingleton<IPostReportActionWorkflow, RemediationPostReportActionWorkflow>();

        // The approval half of the same feature. It is registered where the other external-action
        // adapters are, and only here, because this is the one host that both proposes an approved
        // code write and dispatches it; the Api composes neither. Its descriptor is registered from
        // AddApplication instead, since a configuration naming the tool has to validate everywhere.
        services.AddScoped<RemediationApplyActionTool>();
        services.AddScoped<IExternalActionTool>(
            serviceProvider => serviceProvider.GetRequiredService<RemediationApplyActionTool>());
        services.AddSingleton<TelegramNotificationActionTool>();
        services.AddSingleton<IExternalActionTool>(
            serviceProvider => serviceProvider.GetRequiredService<TelegramNotificationActionTool>());
        services.AddScoped<GitHubIssuesTicketCreate>();
        services.AddScoped<IExternalActionTool>(
            serviceProvider => serviceProvider.GetRequiredService<GitHubIssuesTicketCreate>());
        services.AddScoped<GitHubIssueCommentExternalActionTool>();
        services.AddScoped<IExternalActionTool>(serviceProvider =>
            serviceProvider.GetRequiredService<GitHubIssueCommentExternalActionTool>());

        services.AddScoped<IUserContext>(
            serviceProvider => serviceProvider.GetRequiredService<IBackgroundUserContext>());
        services.TryAddSingleton<WorkerJobLeaseRenewer>();
        services.TryAddSingleton<WorkerJobPump>();
        services.TryAddSingleton<WorkerActionPump>();
        services.TryAddSingleton<PostReportActionEvaluationLeaseRenewer>();
        services.TryAddSingleton<PostReportActionEvaluationPump>();
        services.TryAddSingleton<RetentionPump>();
        services.AddHostedService<TelegramConfigurationStartupValidator>();
        services.AddHostedService<GitHubIssueConfigurationStartupValidator>();
        services.AddHostedService<Worker>();
        services.AddHostedService<ActionDispatchWorker>();
        services.AddHostedService<PostReportActionEvaluationWorker>();
        services.AddHostedService<RetentionWorker>();

        // Last, so it sees every registration: the Worker host must not start with the deferred
        // "not configured" placeholders still bound for the investigation and re-triage ports.
        services.ValidateApplicationWiring();

        return services;
    }
}
