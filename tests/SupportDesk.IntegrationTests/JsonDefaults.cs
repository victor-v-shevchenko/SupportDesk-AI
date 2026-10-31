using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportDesk.IntegrationTests;

/// <summary>Reads API responses the way the API writes them: camelCase, enums as strings.</summary>
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
