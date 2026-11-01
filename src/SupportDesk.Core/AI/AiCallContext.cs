using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>
/// What a model call is for. Features put it in <see cref="ChatOptions.AdditionalProperties"/> with <see cref="ApplyTo"/>,
/// and middleware and the fake provider read it back with <see cref="From"/> (PLAN.md 6.2).
/// </summary>
public sealed record AiCallContext(AiFeature Feature, string PromptVersion, int? TicketId = null, string? TicketNumber = null, Guid? ReplyRunId = null)
{
    public const string FeatureKey = "supportdesk.feature";
    public const string PromptVersionKey = "supportdesk.promptVersion";
    public const string TicketIdKey = "supportdesk.ticketId";
    public const string TicketNumberKey = "supportdesk.ticketNumber";
    public const string ReplyRunIdKey = "supportdesk.replyRunId";

    /// <summary>Writes the context into <paramref name="options"/> and returns it, replacing any earlier context.</summary>
    public ChatOptions ApplyTo(ChatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var properties = options.AdditionalProperties ??= [];
        properties[FeatureKey] = Feature;
        properties[PromptVersionKey] = PromptVersion;
        Set(properties, TicketIdKey, TicketId);
        Set(properties, TicketNumberKey, TicketNumber);
        Set(properties, ReplyRunIdKey, ReplyRunId);
        return options;
    }

    /// <summary>The context in <paramref name="options"/>, or null when the caller didn't set one.</summary>
    public static AiCallContext? From(ChatOptions? options)
    {
        var properties = options?.AdditionalProperties;
        if (properties is null
            || !properties.TryGetValue(FeatureKey, out AiFeature feature)
            || !properties.TryGetValue(PromptVersionKey, out string? promptVersion))
        {
            return null;
        }

        return new AiCallContext(
            feature,
            promptVersion,
            properties.TryGetValue(TicketIdKey, out int ticketId) ? ticketId : null,
            properties.TryGetValue(TicketNumberKey, out string? ticketNumber) ? ticketNumber : null,
            properties.TryGetValue(ReplyRunIdKey, out Guid replyRunId) ? replyRunId : null);
    }

    private static void Set(AdditionalPropertiesDictionary properties, string key, object? value)
    {
        if (value is null)
        {
            properties.Remove(key);
        }
        else
        {
            properties[key] = value;
        }
    }
}
