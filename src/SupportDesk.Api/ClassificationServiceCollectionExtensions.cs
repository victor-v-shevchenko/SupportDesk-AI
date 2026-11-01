using SupportDesk.Core.Classification;

namespace SupportDesk.Api;

public static class ClassificationServiceCollectionExtensions
{
    /// <summary>Ticket classification with structured output (PLAN.md 9), over the chat pipeline and the ticket repository.</summary>
    public static IServiceCollection AddSupportDeskClassification(this IServiceCollection services)
    {
        services.AddScoped<IClassificationService, ClassificationService>();
        return services;
    }
}
