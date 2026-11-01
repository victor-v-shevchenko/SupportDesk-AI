using Microsoft.Extensions.AI;

namespace SupportDesk.Core.AI;

public enum ChatProvider
{
    Fake,
    Ollama,
    Anthropic,
    OpenAI,
}

public enum EmbeddingProvider
{
    Fake,
    Ollama,
    Voyage,
    OpenAI,
}

/// <summary>The <c>AI</c> section (PLAN.md 6.1). Checked at startup by <see cref="AiOptionsValidator"/>.</summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    public ChatModelSettings Chat { get; set; } = new();

    public EmbeddingModelSettings Embeddings { get; set; } = new();

    public OllamaSettings Ollama { get; set; } = new();

    public AiTimeoutSettings Timeouts { get; set; } = new();

    public AiRetrySettings Retry { get; set; } = new();

    public AiBudgetSettings Budget { get; set; } = new();

    /// <summary>
    /// USD per million tokens (PLAN.md 6.6). A list rather than <c>AI:Pricing:&lt;model&gt;</c>, because configuration keys
    /// use <c>:</c> as a separator and Ollama model names (<c>qwen2.5:1.5b</c>) contain one.
    /// </summary>
    public List<ModelPrice> Pricing { get; set; } = [];

    public AiUsageSettings Usage { get; set; } = new();

    /// <summary>Prompt name to version, for example <c>reply</c> → <c>reply.v1</c> (loads <c>prompts/reply.v1.md</c>).</summary>
    public Dictionary<string, string> Prompts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The prompts folder; a relative path is resolved from the app's base directory, where the build copies <c>prompts/</c>.</summary>
    public string PromptsPath { get; set; } = "prompts";

    public AnthropicSettings Anthropic { get; set; } = new();

    public FakeProviderSettings Fake { get; set; } = new();

    public string ResolvedPromptsPath => Path.GetFullPath(PromptsPath, AppContext.BaseDirectory);
}

public sealed class ChatModelSettings
{
    public ChatProvider Provider { get; set; } = ChatProvider.Ollama;

    /// <summary>Empty means the provider's default (<see cref="ResolvedModel"/>); OpenAI has none and needs it set.</summary>
    public string? Model { get; set; }

    public string? ResolvedModel => !string.IsNullOrWhiteSpace(Model) ? Model : Provider switch
    {
        ChatProvider.Fake => "fake",
        ChatProvider.Ollama => "qwen2.5:1.5b",
        ChatProvider.Anthropic => "claude-opus-5-5",
        _ => null,
    };
}

public sealed class EmbeddingModelSettings
{
    public EmbeddingProvider Provider { get; set; } = EmbeddingProvider.Ollama;

    /// <summary>Empty means the provider's default (<see cref="ResolvedModel"/>).</summary>
    public string? Model { get; set; }

    /// <summary>The <c>vector(1024)</c> column fixes this; every provider is asked for (or produces) this many.</summary>
    public int Dimensions { get; set; } = 1024;

    public string ResolvedModel => !string.IsNullOrWhiteSpace(Model) ? Model : Provider switch
    {
        EmbeddingProvider.Fake => "fake-hash",
        EmbeddingProvider.Voyage => "voyage-3.5",
        EmbeddingProvider.OpenAI => "text-embedding-3-small",
        _ => "mxbai-embed-large",
    };
}

public sealed class OllamaSettings
{
    /// <summary>Set in appsettings.json (Ollama on the host, plain HTTP); required when Ollama is the chat or embedding provider.</summary>
    public Uri? Endpoint { get; set; }
}

public sealed class AiTimeoutSettings
{
    /// <summary>The only timeout on <see cref="HttpClient"/>; the rest are in <see cref="RetryTimeoutMiddleware"/>.</summary>
    public int ConnectSeconds { get; set; } = 10;

    /// <summary>One non-streaming attempt.</summary>
    public int NonStreamingSeconds { get; set; } = 30;

    /// <summary>From the start of a streaming attempt to its first text or tool call.</summary>
    public int FirstItemSeconds { get; set; } = 20;

    /// <summary>Between two updates once something has arrived.</summary>
    public int GapSeconds { get; set; } = 15;

    /// <summary>A whole streaming call, retries included.</summary>
    public int StreamTotalSeconds { get; set; } = 120;
}

public sealed class AiRetrySettings
{
    /// <summary>Attempts in all, so 3 is one call and two retries.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>The first backoff; it doubles per attempt, with jitter.</summary>
    public int BaseDelayMs { get; set; } = 500;

    /// <summary>The longest wait before a retry. A provider's <c>Retry-After</c> longer than this ends the call instead of waiting.</summary>
    public int MaxDelayMs { get; set; } = 10_000;
}

public sealed class AiBudgetSettings
{
    public int MaxInputTokensPerCall { get; set; } = 6000;

    /// <summary>Set on a call when the caller didn't set <c>MaxOutputTokens</c>.</summary>
    public Dictionary<AiFeature, int> MaxOutputTokens { get; set; } = new()
    {
        [AiFeature.Reply] = 800,
        [AiFeature.Summary] = 200,
        [AiFeature.Classification] = 200,
    };

    /// <summary>Input and output tokens across all the calls of one request (the tool loop).</summary>
    public int MaxTokensPerRequest { get; set; } = 20_000;
}

public sealed class ModelPrice
{
    public string Model { get; set; } = "";

    public decimal InputPerMTok { get; set; }

    public decimal OutputPerMTok { get; set; }
}

public sealed class AiUsageSettings
{
    /// <summary>Model call rows waiting for the database; when it's full, new rows are dropped with a warning.</summary>
    public int QueueCapacity { get; set; } = 1000;
}

public sealed class AnthropicSettings
{
    /// <summary>Effort per feature (PLAN.md 6.7). <see cref="ReasoningEffort.None"/> isn't allowed: it turns thinking off, which current models reject.</summary>
    public Dictionary<AiFeature, ReasoningEffort> Effort { get; set; } = new()
    {
        [AiFeature.Reply] = ReasoningEffort.Medium,
        [AiFeature.Summary] = ReasoningEffort.Low,
        [AiFeature.Classification] = ReasoningEffort.Low,
    };
}

public sealed class FakeProviderSettings
{
    /// <summary>The scenarios file (PLAN.md 6.8); a relative path is resolved from the app's base directory. Tests point this at their own file.</summary>
    public string ScenariosPath { get; set; } = "fixtures/fake-scenarios.json";

    public string ResolvedScenariosPath => Path.GetFullPath(ScenariosPath, AppContext.BaseDirectory);
}
