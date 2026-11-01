using SupportDesk.Core.Replies;
using SupportDesk.Core.Replies.Tools;

namespace SupportDesk.Api;

public static class ReplyServiceCollectionExtensions
{
    /// <summary>
    /// The suggested reply (PLAN.md 7, 8) and its tools (PLAN.md 10), scoped like the retrieval service and the repository
    /// they use, and the background check that expires old proposals.
    /// </summary>
    public static IServiceCollection AddSupportDeskReplies(this IServiceCollection services)
    {
        services.AddScoped<IReplyService, ReplyService>();
        services.AddScoped<ReplyToolFactory>();
        services.AddSingleton<ProposalExpiryService>();
        services.AddHostedService(provider => provider.GetRequiredService<ProposalExpiryService>());
        return services;
    }
}
