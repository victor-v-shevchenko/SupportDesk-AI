using SupportDesk.Core.Summaries;

namespace SupportDesk.Api;

public static class SummaryServiceCollectionExtensions
{
    /// <summary>The ticket summary (PLAN.md 9); scoped, like the ticket repository it saves through.</summary>
    public static IServiceCollection AddSupportDeskSummary(this IServiceCollection services)
    {
        services.AddScoped<ISummaryService, SummaryService>();
        return services;
    }
}
