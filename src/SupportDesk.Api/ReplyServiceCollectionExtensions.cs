using SupportDesk.Core.Replies;

namespace SupportDesk.Api;

public static class ReplyServiceCollectionExtensions
{
    /// <summary>The suggested reply (PLAN.md 7, 8), scoped like the retrieval service it uses.</summary>
    public static IServiceCollection AddSupportDeskReplies(this IServiceCollection services)
    {
        services.AddScoped<IReplyService, ReplyService>();
        return services;
    }
}
