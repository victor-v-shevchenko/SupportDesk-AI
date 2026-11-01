namespace SupportDesk.Api.AI;

/// <summary>
/// Provider API keys, read from user secrets or <c>AI__…__ApiKey</c> environment variables (docker compose maps them from
/// <c>.env</c>), never from appsettings. Not <c>ANTHROPIC_API_KEY</c>: the dev container keeps that variable out of its
/// environment, so Claude Code running in it doesn't pick it up.
/// </summary>
public sealed class ProviderKeys
{
    public const string AnthropicSetting = "AI:Anthropic:ApiKey";
    public const string OpenAISetting = "AI:OpenAI:ApiKey";
    public const string VoyageSetting = "AI:Voyage:ApiKey";

    public string? Anthropic { get; set; }

    public string? OpenAI { get; set; }

    public string? Voyage { get; set; }

    public static ProviderKeys From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new ProviderKeys
        {
            Anthropic = configuration[AnthropicSetting],
            OpenAI = configuration[OpenAISetting],
            Voyage = configuration[VoyageSetting],
        };
    }

    /// <summary>The key, or an exception naming the setting (and never a value).</summary>
    public static string Require(string? key, string setting) =>
        string.IsNullOrWhiteSpace(key) ? throw new InvalidOperationException($"{setting} is not set.") : key;
}
