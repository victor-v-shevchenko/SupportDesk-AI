namespace SupportDesk.Core.AI.Usage;

public enum ModelCallOutcome
{
    Success,
    Error,
    Cancelled,
}

/// <summary>One call to the provider as the feature saw it, retries included (PLAN.md 4, 6.6). Written by <see cref="UsageLoggingMiddleware"/>.</summary>
public sealed class ModelCall
{
    public const int ProviderMaxLength = 50;
    public const int ModelMaxLength = 100;
    public const int PromptVersionMaxLength = 50;

    public long Id { get; set; }

    /// <summary>When the call started.</summary>
    public DateTimeOffset Timestamp { get; set; }

    public AiFeature Feature { get; set; }

    public required string Provider { get; set; }

    public required string Model { get; set; }

    public required string PromptVersion { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    /// <summary>Null when the model isn't in <c>AI:Pricing</c>; never a guessed price.</summary>
    public decimal? CostUsd { get; set; }

    public long LatencyMs { get; set; }

    /// <summary>The provider sent no usage (the call failed or was cancelled first), so the token counts are estimates.</summary>
    public bool IsEstimated { get; set; }

    public ModelCallOutcome Outcome { get; set; }

    public int? TicketId { get; set; }

    public Guid? ReplyRunId { get; set; }
}

public interface IModelCallRepository
{
    Task AddRangeAsync(IReadOnlyList<ModelCall> calls, CancellationToken cancellationToken);

    /// <summary>Newest first, at most <paramref name="limit"/> rows.</summary>
    Task<IReadOnlyList<ModelCall>> ListAsync(int? ticketId, int limit, CancellationToken cancellationToken);
}
