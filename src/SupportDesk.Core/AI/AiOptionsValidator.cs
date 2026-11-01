using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI.Fake;
using SupportDesk.Core.AI.Prompts;

namespace SupportDesk.Core.AI;

/// <summary>
/// Checks the <c>AI</c> section at startup, including that every configured prompt and, with the fake provider, the
/// scenarios file load. Provider keys and the environment are checked in the API, which owns them.
/// </summary>
public sealed class AiOptionsValidator : IValidateOptions<AiOptions>
{
    /// <summary>The prompts the features use; each needs an <c>AI:Prompts</c> entry.</summary>
    public static readonly IReadOnlyList<string> RequiredPrompts = [PromptNames.Reply, PromptNames.Summary, PromptNames.Classify];

    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (!Enum.IsDefined(options.Chat.Provider))
        {
            errors.Add($"AI:Chat:Provider '{options.Chat.Provider}' isn't one of {string.Join(", ", Enum.GetNames<ChatProvider>())}.");
        }
        else if (options.Chat.ResolvedModel is null)
        {
            errors.Add($"AI:Chat:Model is required when AI:Chat:Provider is {options.Chat.Provider}.");
        }

        if (!Enum.IsDefined(options.Embeddings.Provider))
        {
            errors.Add($"AI:Embeddings:Provider '{options.Embeddings.Provider}' isn't one of {string.Join(", ", Enum.GetNames<EmbeddingProvider>())}.");
        }

        if (options.Embeddings.Dimensions != 1024)
        {
            errors.Add("AI:Embeddings:Dimensions must be 1024, the size of the embedding column.");
        }

        var usesOllama = options.Chat.Provider == ChatProvider.Ollama || options.Embeddings.Provider == EmbeddingProvider.Ollama;
        if (usesOllama && options.Ollama.Endpoint is not { IsAbsoluteUri: true, Scheme: "http" or "https" })
        {
            errors.Add("AI:Ollama:Endpoint must be an absolute http or https URL when Ollama is a provider.");
        }

        Range(errors, "AI:Timeouts:ConnectSeconds", options.Timeouts.ConnectSeconds, 1, 120);
        Range(errors, "AI:Timeouts:NonStreamingSeconds", options.Timeouts.NonStreamingSeconds, 1, 600);
        Range(errors, "AI:Timeouts:FirstItemSeconds", options.Timeouts.FirstItemSeconds, 1, 600);
        Range(errors, "AI:Timeouts:GapSeconds", options.Timeouts.GapSeconds, 1, 600);
        Range(errors, "AI:Timeouts:StreamTotalSeconds", options.Timeouts.StreamTotalSeconds, 1, 3600);
        Range(errors, "AI:Retry:MaxAttempts", options.Retry.MaxAttempts, 1, 10);
        Range(errors, "AI:Retry:BaseDelayMs", options.Retry.BaseDelayMs, 0, 60_000);
        Range(errors, "AI:Retry:MaxDelayMs", options.Retry.MaxDelayMs, 0, 300_000);
        Range(errors, "AI:Budget:MaxInputTokensPerCall", options.Budget.MaxInputTokensPerCall, 1, 1_000_000);
        Range(errors, "AI:Budget:MaxTokensPerRequest", options.Budget.MaxTokensPerRequest, 1, 10_000_000);
        Range(errors, "AI:Usage:QueueCapacity", options.Usage.QueueCapacity, 1, 1_000_000);
        foreach (var (feature, maxOutput) in options.Budget.MaxOutputTokens)
        {
            Range(errors, $"AI:Budget:MaxOutputTokens:{feature}", maxOutput, 1, 100_000);
        }

        CheckPricing(errors, options.Pricing);
        CheckEffort(errors, options.Anthropic.Effort);
        CheckPrompts(errors, options);

        if (options.Chat.Provider == ChatProvider.Fake)
        {
            TryLoad(errors, () => FakeScenarioSet.Load(options.Fake.ResolvedScenariosPath));
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void CheckPricing(List<string> errors, List<ModelPrice> pricing)
    {
        foreach (var duplicate in pricing.GroupBy(p => p.Model, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"AI:Pricing lists {duplicate.Key} more than once.");
        }

        if (pricing.Any(p => string.IsNullOrWhiteSpace(p.Model) || p.InputPerMTok < 0 || p.OutputPerMTok < 0))
        {
            errors.Add("Every AI:Pricing entry needs a Model and prices that aren't negative.");
        }
    }

    private static void CheckEffort(List<string> errors, Dictionary<AiFeature, ReasoningEffort> effort)
    {
        foreach (var (feature, level) in effort)
        {
            if (!Enum.IsDefined(level) || level == ReasoningEffort.None)
            {
                errors.Add($"AI:Anthropic:Effort:{feature} must be Low, Medium, High or ExtraHigh; None turns thinking off, which current models reject.");
            }
        }
    }

    private static void CheckPrompts(List<string> errors, AiOptions options)
    {
        errors.AddRange(RequiredPrompts.Where(p => !options.Prompts.ContainsKey(p)).Select(p => $"AI:Prompts:{p} is required."));
        foreach (var (prompt, version) in options.Prompts)
        {
            // An empty value in config binds as null
            if (string.IsNullOrWhiteSpace(version))
            {
                errors.Add($"AI:Prompts:{prompt} is empty; set it to a version such as '{prompt}.v1'.");
                continue;
            }

            TryLoad(errors, () => PromptStore.Load(options.ResolvedPromptsPath, prompt, version));
        }
    }

    private static void Range(List<string> errors, string setting, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{setting} must be between {min} and {max}.");
        }
    }

    private static void TryLoad(List<string> errors, Action load)
    {
        try
        {
            load();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            errors.Add(exception.Message);
        }
    }
}

public static class PromptNames
{
    public const string Reply = "reply";
    public const string Summary = "summary";
    public const string Classify = "classify";
}
