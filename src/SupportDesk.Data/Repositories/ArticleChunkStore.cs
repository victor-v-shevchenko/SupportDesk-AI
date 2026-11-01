using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using SupportDesk.Core.Retrieval;

namespace SupportDesk.Data.Repositories;

internal sealed class ArticleChunkStore(SupportDeskDbContext db) : IArticleChunkStore
{
    public async Task<IReadOnlyList<StoredChunk>> ListAsync(CancellationToken cancellationToken) =>
        await db.ArticleChunks.AsNoTracking()
            .Select(c => new StoredChunk(c.Id, c.ArticleId, c.Ordinal, c.ContentHash, c.EmbeddingModel))
            .ToListAsync(cancellationToken);

    public async Task SaveAsync(ChunkChanges changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (changes.Deleted.Count > 0)
        {
            var deleted = changes.Deleted.ToList();
            await db.ArticleChunks.Where(c => deleted.Contains(c.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        foreach (var move in changes.Moved)
        {
            await db.ArticleChunks.Where(c => c.Id == move.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Ordinal, move.Ordinal), cancellationToken);
        }

        db.ArticleChunks.AddRange(changes.Added.Select(a => new ArticleChunk
        {
            ArticleId = a.ArticleId,
            Ordinal = a.Chunk.Ordinal,
            Heading = a.Chunk.Heading,
            Text = a.Chunk.Text,
            ContentHash = a.Chunk.ContentHash,
            EmbeddingModel = a.EmbeddingModel,
            Embedding = new Vector(a.Embedding),
        }));
        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(
        ReadOnlyMemory<float> vector, string embeddingModel, int k, CancellationToken cancellationToken)
    {
        var query = new Vector(vector);

        // CosineDistance translates to <=>, the operator the HNSW index (vector_cosine_ops) serves
        var rows = await db.ArticleChunks.AsNoTracking()
            .Where(c => c.EmbeddingModel == embeddingModel)
            .OrderBy(c => c.Embedding.CosineDistance(query))
            .Take(k)
            .Select(c => new { c.Article!.Slug, c.Article.Title, c.Heading, c.Text, Distance = c.Embedding.CosineDistance(query) })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => new RetrievedChunk(r.Slug, r.Title, r.Heading, r.Text, 1 - r.Distance))];
    }
}
