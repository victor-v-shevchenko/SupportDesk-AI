using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;

namespace SupportDesk.Api.AI;

/// <summary>
/// The startup checks that need the API's view: the selected providers' keys are set, and the fake provider isn't used in
/// <c>Production</c>. Messages name the setting, never its value.
/// </summary>
public sealed class ProviderOptionsValidator(IOptions<ProviderKeys> keys, IHostEnvironment environment) : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        var k = keys.Value;

        if (environment.IsProduction())
        {
            if (options.Chat.Provider == ChatProvider.Fake)
            {
                errors.Add("AI:Chat:Provider is Fake, which isn't allowed in Production.");
            }

            if (options.Embeddings.Provider == EmbeddingProvider.Fake)
            {
                errors.Add("AI:Embeddings:Provider is Fake, which isn't allowed in Production.");
            }
        }

        RequireKey(errors, options.Chat.Provider == ChatProvider.Anthropic, k.Anthropic, ProviderKeys.AnthropicSetting, "AI:Chat:Provider is Anthropic");
        RequireKey(errors, options.Chat.Provider == ChatProvider.OpenAI, k.OpenAI, ProviderKeys.OpenAISetting, "AI:Chat:Provider is OpenAI");
        RequireKey(errors, options.Embeddings.Provider == EmbeddingProvider.OpenAI, k.OpenAI, ProviderKeys.OpenAISetting, "AI:Embeddings:Provider is OpenAI");
        RequireKey(errors, options.Embeddings.Provider == EmbeddingProvider.Voyage, k.Voyage, ProviderKeys.VoyageSetting, "AI:Embeddings:Provider is Voyage");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors.Distinct(StringComparer.Ordinal));
    }

    private static void RequireKey(List<string> errors, bool selected, string? key, string setting, string reason)
    {
        if (selected && string.IsNullOrWhiteSpace(key))
        {
            errors.Add($"{setting} is required when {reason}. Set it with user secrets or the {setting.Replace(":", "__", StringComparison.Ordinal)} environment variable.");
        }
    }
}
