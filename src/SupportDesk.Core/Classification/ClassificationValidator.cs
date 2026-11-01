using System.Text.Json;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Core.Classification;

/// <summary>The outcome of checking a model answer: a result, or the reasons it was rejected.</summary>
public sealed record ClassificationValidation(ClassificationResult? Result, IReadOnlyList<string> Errors)
{
    public bool IsValid => Result is not null;

    /// <summary>The errors as one line, for the retry message and debug logs.</summary>
    public string ErrorText => string.Join(" ", Errors);
}

/// <summary>
/// Checks a model answer against the classification schema (PLAN.md 9) on the raw text, whatever the provider claims to
/// enforce: one JSON object with exactly <c>category</c> (an enum name), <c>priority</c> (an enum name) and <c>reason</c>
/// (a non-empty string of at most 200 characters), with no duplicate or extra properties.
/// </summary>
public static class ClassificationValidator
{
    public const string CategoryProperty = "category";
    public const string PriorityProperty = "priority";
    public const string ReasonProperty = "reason";

    private static readonly string[] Properties = [CategoryProperty, PriorityProperty, ReasonProperty];

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    public static ClassificationValidation Validate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Invalid("The answer was empty; expected a JSON object.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, DocumentOptions);
        }
        catch (JsonException)
        {
            return Invalid("The answer isn't valid JSON; expected only a JSON object, with no other text.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid($"The answer is a JSON {Kind(root)}; expected a JSON object.");
            }

            var errors = new List<string>();
            foreach (var property in root.EnumerateObject())
            {
                if (!Properties.Contains(property.Name, StringComparer.Ordinal))
                {
                    // Not named: the errors go back to the model, and nothing it wrote is repeated (it can echo the ticket)
                    errors.Add($"The answer has a property other than {CategoryProperty}, {PriorityProperty} and {ReasonProperty}; no others are allowed.");
                    break;
                }
            }

            var category = ReadEnum<Category>(root, CategoryProperty, errors);
            var priority = ReadEnum<Priority>(root, PriorityProperty, errors);
            var reason = ReadReason(root, errors);

            return errors.Count == 0
                ? new ClassificationValidation(new ClassificationResult(category!.Value, priority!.Value, reason!), [])
                : new ClassificationValidation(null, errors);
        }
    }

    private static TEnum? ReadEnum<TEnum>(JsonElement root, string name, List<string> errors)
        where TEnum : struct, Enum
    {
        var allowed = $"{name} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}";
        if (!root.TryGetProperty(name, out var value))
        {
            errors.Add($"Missing property \"{name}\"; {allowed}.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{allowed}, as a string; got a JSON {Kind(value)}.");
            return null;
        }

        // Exact names only: Enum.TryParse would also take numbers, other casing and "A, B"
        var text = value.GetString()!;
        if (!Enum.GetNames<TEnum>().Contains(text, StringComparer.Ordinal))
        {
            errors.Add($"{allowed}; got another value.");
            return null;
        }

        return Enum.Parse<TEnum>(text);
    }

    private static string? ReadReason(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty(ReasonProperty, out var value))
        {
            errors.Add($"Missing property \"{ReasonProperty}\"; it must be one sentence of at most {ClassificationResult.ReasonMaxLength} characters.");
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{ReasonProperty} must be a string; got a JSON {Kind(value)}.");
            return null;
        }

        var reason = value.GetString()!.Trim();
        if (reason.Length == 0)
        {
            errors.Add($"{ReasonProperty} is empty; it must be one sentence of at most {ClassificationResult.ReasonMaxLength} characters.");
            return null;
        }

        if (reason.Length > ClassificationResult.ReasonMaxLength)
        {
            errors.Add($"{ReasonProperty} is {reason.Length} characters long; it must be at most {ClassificationResult.ReasonMaxLength}.");
            return null;
        }

        return reason;
    }

    private static ClassificationValidation Invalid(string error) => new(null, [error]);

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.String => "string",
        _ => "object",
    };
}
