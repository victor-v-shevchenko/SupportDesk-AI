using System.Text.Json;
using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

/// <summary>
/// The token estimate from PLAN.md 6.4: characters ÷ 4 × 1.2, rounded up. Errs high on purpose; an opt-in test in
/// <c>SupportDesk.Eval</c> checks it against Anthropic's token counter.
/// </summary>
public static class TokenEstimator
{
    /// <summary>Everything a call sends: every message's text, tool calls and results, and the tool definitions.</summary>
    public static long EstimateInput(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var characters = messages.Sum(m => Characters(m.Contents));
        foreach (var tool in options?.Tools ?? [])
        {
            characters += tool.Name.Length + tool.Description.Length;
            if (tool is AIFunctionDeclaration function)
            {
                characters += function.JsonSchema.GetRawText().Length;
            }
        }

        return FromCharacters(characters);
    }

    /// <summary>What came back, for a call whose provider sent no usage.</summary>
    public static long EstimateOutput(IEnumerable<AIContent> contents) => FromCharacters(Characters(contents));

    /// <summary>The characters in <paramref name="contents"/> that count toward tokens: text, thinking, tool calls and results.</summary>
    public static long Characters(IEnumerable<AIContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return contents.Sum(CharactersIn);
    }

    public static long FromCharacters(long characters) => (long)Math.Ceiling(characters / 4.0 * 1.2);

    private static long CharactersIn(AIContent content) => content switch
    {
        TextContent text => (long)text.Text.Length,
        TextReasoningContent reasoning => reasoning.Text.Length,
        FunctionCallContent call => call.Name.Length + (call.Arguments is null ? 0 : JsonSerializer.Serialize(call.Arguments, AIJsonUtilities.DefaultOptions).Length),
        FunctionResultContent result => result.Result is null ? 0 : JsonSerializer.Serialize(result.Result, AIJsonUtilities.DefaultOptions).Length,
        _ => 0L,
    };
}

/// <summary>One call's tokens: the provider's usage where it sent it, the estimate where it didn't.</summary>
public readonly record struct CallTokens(long Input, long Output, bool IsEstimated)
{
    public long Total => Input + Output;

    public static CallTokens Resolve(UsageDetails? usage, IEnumerable<ChatMessage> messages, ChatOptions? options, IEnumerable<AIContent> output) =>
        Resolve(usage, messages, options, TokenEstimator.Characters(output));

    public static CallTokens Resolve(UsageDetails? usage, IEnumerable<ChatMessage> messages, ChatOptions? options, long outputCharacters)
    {
        var input = usage?.InputTokenCount;
        var outputTokens = usage?.OutputTokenCount;
        return new CallTokens(
            input ?? TokenEstimator.EstimateInput(messages, options),
            outputTokens ?? TokenEstimator.FromCharacters(outputCharacters),
            input is null || outputTokens is null);
    }
}

/// <summary>
/// Collects a stream's usage and output size as it passes. Usage updates are added up, which is how
/// Microsoft.Extensions.AI combines them (<c>ToChatResponse</c>).
/// </summary>
public sealed class StreamTokens
{
    private UsageDetails? _usage;
    private long _outputCharacters;

    public UsageDetails? Usage => _usage;

    public long OutputCharacters => _outputCharacters;

    public void Observe(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        foreach (var content in update.Contents)
        {
            if (content is UsageContent usage)
            {
                _usage ??= new UsageDetails();
                _usage.Add(usage.Details);
            }
        }

        _outputCharacters += TokenEstimator.Characters(update.Contents);
    }

    public CallTokens Resolve(IEnumerable<ChatMessage> messages, ChatOptions? options) =>
        CallTokens.Resolve(_usage, messages, options, _outputCharacters);
}
