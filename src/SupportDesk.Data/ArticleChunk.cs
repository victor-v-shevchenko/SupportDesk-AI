using Pgvector;
using SupportDesk.Core.Articles;

namespace SupportDesk.Data;

/// <summary>
/// One embedded piece of an article (PLAN.md 4 and 8). Lives in Data rather than Core because its vector is pgvector's
/// <see cref="Vector"/>; Core sees chunks through <see cref="Core.Retrieval.IArticleChunkStore"/>.
/// </summary>
public sealed class ArticleChunk
{
    public const int EmbeddingModelMaxLength = 100;

    public long Id { get; set; }

    public int ArticleId { get; set; }

    public Article? Article { get; set; }

    public int Ordinal { get; set; }

    /// <summary>Null for the text before an article's first <c>##</c> heading.</summary>
    public string? Heading { get; set; }

    public required string Text { get; set; }

    /// <summary>Lowercase hex SHA-256 of the embedded text (heading, newline, text).</summary>
    public required string ContentHash { get; set; }

    public required string EmbeddingModel { get; set; }

    public required Vector Embedding { get; set; }
}
