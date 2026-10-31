using System.Text.Json.Serialization;

namespace SupportDesk.Api;

public static class ApiServiceCollectionExtensions
{
    /// <summary>HTTP-level services: problem details, and enums as strings in JSON (<c>"Open"</c>, not <c>0</c>).</summary>
    public static IServiceCollection AddSupportDeskApi(this IServiceCollection services)
    {
        services.AddProblemDetails();
        // In Development, Minimal APIs throw on an unreadable request body; keep its 400 instead of turning it into a 500
        services.Configure<ExceptionHandlerOptions>(options =>
            options.StatusCodeSelector = exception => exception is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError);
        services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
        return services;
    }
}
