using Microsoft.Extensions.Configuration;
using SupportDesk.Core.AI;

namespace SupportDesk.Eval.Suite;

/// <summary>
/// The chat provider and model the run measures, read the way the API reads them in Development (appsettings, user
/// secrets, then <c>AI__…</c> variables), the optional <c>Eval:Limit</c> for a dry run on the first few tickets, and
/// <c>Eval:SaveReplies</c>, which keeps each reply next to the test assembly so the approximate metrics can be checked, and
/// <c>Eval:Label</c>, which goes into the report's file name so a second run on the same day doesn't replace the first.
/// </summary>
public sealed record EvalSettings(ChatProvider Provider, string Model, IReadOnlyDictionary<string, string> Prompts, int? Limit, bool SaveReplies = false, string? Label = null)
{
    public static EvalSettings Load()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json"), optional: true)
            .AddUserSecrets<EvalSettings>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        return From(configuration);
    }

    /// <summary>Throws when the provider is <c>Fake</c>, since measuring the fake model would only measure its scenarios.</summary>
    public static EvalSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var ai = configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
        if (ai.Chat.Provider == ChatProvider.Fake)
        {
            throw new InvalidOperationException(
                "The evaluation measures a real model, but AI:Chat:Provider is Fake. Set AI__Chat__Provider to Ollama, Anthropic or OpenAI.");
        }

        var model = ai.Chat.ResolvedModel
            ?? throw new InvalidOperationException($"AI:Chat:Model is required for {ai.Chat.Provider}.");
        // The two prompts the run uses; their system prompts are what a reply must not repeat
        var prompts = new[] { PromptNames.Classify, PromptNames.Reply }.ToDictionary(
            p => p,
            p => configuration[$"{AiOptions.SectionName}:Prompts:{p}"] ?? throw new InvalidOperationException($"AI:Prompts:{p} is required."),
            StringComparer.Ordinal);
        var limit = configuration.GetValue<int?>("Eval:Limit");
        if (limit is <= 0)
        {
            throw new InvalidOperationException("Eval:Limit must be a positive number of tickets.");
        }

        return new EvalSettings(ai.Chat.Provider, model, prompts, limit, configuration.GetValue<bool>("Eval:SaveReplies"), configuration["Eval:Label"]);
    }
}
