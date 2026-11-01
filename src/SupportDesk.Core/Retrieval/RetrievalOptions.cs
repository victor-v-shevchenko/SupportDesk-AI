using System.ComponentModel.DataAnnotations;

namespace SupportDesk.Core.Retrieval;

/// <summary>The <c>Retrieval</c> section (PLAN.md 8).</summary>
public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>Results less similar than this are dropped. Tuned against the retrieval test questions (PLAN.md 8).</summary>
    [Range(0.0, 1.0)]
    public double MinSimilarity { get; set; } = 0.5;

    /// <summary>How many chunks go to the embedding provider in one request during the startup sync.</summary>
    [Range(1, 256)]
    public int EmbeddingBatchSize { get; set; } = 16;
}
