using SupportDesk.Core.AI;

namespace SupportDesk.Eval;

/// <summary>
/// Picks the model Anthropic's token counter is asked about. The configured chat model counts only when the chat
/// provider is Anthropic; another provider's model (such as qwen2.5:7b) is not one Anthropic knows.
/// </summary>
public static class TokenCountModel
{
    public const string Default = "claude-opus-5-5";

    public static string Choose(string? provider, string? model) =>
        Enum.TryParse<ChatProvider>(provider, ignoreCase: true, out var parsed) && parsed == ChatProvider.Anthropic && model is { Length: > 0 }
            ? model
            : Default;
}
