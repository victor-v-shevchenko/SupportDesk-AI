using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Classification;

/// <summary>
/// What the model returns for a classification (PLAN.md 9). <see cref="ClassificationJson.Options"/> gives it the JSON shape
/// the schema asks for: camelCase names, enum names as strings, all three properties required and nothing else allowed.
/// </summary>
[Description("The category and priority of a support ticket, with the reason for them.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ClassificationResult(
    [property: Description("The ticket's category.")]
    Category Category,
    [property: Description("The ticket's priority.")]
    Priority Priority,
    [property: Description("One sentence explaining the category and priority.")]
    [property: MaxLength(ClassificationResult.ReasonMaxLength)]
    string Reason)
{
    /// <summary>The same as the <c>ClassificationReason</c> column.</summary>
    public const int ReasonMaxLength = 200;
}

public static class ClassificationJson
{
    /// <summary>
    /// Used to build the schema sent with the request. Reading the answer doesn't rely on these options:
    /// <see cref="ClassificationValidator"/> checks the raw text against the same rules.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        options.MakeReadOnly();
        return options;
    }
}
