using Microsoft.Extensions.Configuration;
using SupportDesk.Core.AI;

namespace SupportDesk.Eval;

/// <summary>
/// Picks the model Anthropic's token counter is asked about. The configured chat model counts only when the chat
/// provider is Anthropic; another provider's model (such as qwen2.5:7b) is not one Anthropic knows. The settings are
/// bound as the app binds them, so a missing provider is the app's default (Ollama) and a blank model is the provider's
/// default.
/// </summary>
public static class TokenCountModel
{
    public static string Default { get; } = new ChatModelSettings { Provider = ChatProvider.Anthropic }.ResolvedModel!;

    public static string Choose(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var chat = configuration.GetSection("AI:Chat").Get<ChatModelSettings>() ?? new ChatModelSettings();
        return chat.Provider == ChatProvider.Anthropic ? chat.ResolvedModel! : Default;
    }
}
