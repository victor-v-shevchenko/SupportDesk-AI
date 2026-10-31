using System.Text.Json.Serialization;

namespace SupportDesk.Api;

public static class ApiServiceCollectionExtensions
{
    /// <summary>HTTP-level services: problem details, and enums as strings in JSON (<c>"Open"</c>, not <c>0</c>).</summary>
    public static IServiceCollection AddSupportDeskApi(this IServiceCollection services)
    {
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
        return services;
    }
}
