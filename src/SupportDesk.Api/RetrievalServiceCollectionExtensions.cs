using SupportDesk.Core.Retrieval;

namespace SupportDesk.Api;

public static class RetrievalServiceCollectionExtensions
{
    /// <summary>Chunk embedding sync and similarity search (PLAN.md 8), over the embedding generator and the chunk store.</summary>
    public static IServiceCollection AddSupportDeskRetrieval(this IServiceCollection services)
    {
        services.AddOptions<RetrievalOptions>()
            .BindConfiguration(RetrievalOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<EmbeddingSyncService>();
        services.AddScoped<IRetrievalService, RetrievalService>();
        return services;
    }
}
