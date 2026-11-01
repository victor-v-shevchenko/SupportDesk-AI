using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SupportDesk.Core.AI.Usage;

/// <summary>Cost from <c>AI:Pricing</c> (PLAN.md 6.6): input tokens × input price + output tokens × output price, per million.</summary>
public sealed partial class PriceTable(IOptions<AiOptions> options, ILogger<PriceTable> logger)
{
    private readonly Dictionary<string, ModelPrice> _prices = options.Value.Pricing.ToDictionary(p => p.Model, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _warned = new(StringComparer.Ordinal);

    /// <summary>Null for a model that has no price, with a warning the first time: an unknown cost is stored as unknown, not guessed.</summary>
    public decimal? Cost(string model, long inputTokens, long outputTokens)
    {
        if (!_prices.TryGetValue(model, out var price))
        {
            if (_warned.TryAdd(model, true))
            {
                LogUnknownModel(model);
            }

            return null;
        }

        return ((inputTokens * price.InputPerMTok) + (outputTokens * price.OutputPerMTok)) / 1_000_000m;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No price for model {Model} in AI:Pricing; its cost is stored as null")]
    private partial void LogUnknownModel(string model);
}
