using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportDesk.Core.AI;
using SupportDesk.Core.Articles;

namespace SupportDesk.Core.Retrieval;

/// <summary>
/// Keeps the stored chunks in step with the articles and the embedding model (PLAN.md 8). Runs at startup after seeding.
/// Each article's chunks are matched to stored rows by content hash and model: a match keeps its vector (its ordinal is
/// updated if sections moved), anything else is embedded, and stored rows left without a match are deleted.
/// </summary>
public sealed partial class EmbeddingSyncService(
    IArticleRepository articles,
    IArticleChunkStore store,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IOptions<AiOptions> aiOptions,
    IOptions<RetrievalOptions> retrievalOptions,
    ILogger<EmbeddingSyncService> logger)
{
    public async Task<EmbeddingSyncResult> SyncAsync(CancellationToken cancellationToken)
    {
        var model = aiOptions.Value.Embeddings.ResolvedModel;
        var stored = (await store.ListAsync(cancellationToken)).ToLookup(c => c.ArticleId);

        var toEmbed = new List<(int ArticleId, ArticleChunkDraft Chunk)>();
        var moved = new List<MovedChunk>();
        var keptIds = new HashSet<long>();
        foreach (var article in await articles.ListAsync(cancellationToken))
        {
            // A queue per hash, so an article with two identical chunks keeps two rows
            var reusable = stored[article.Id]
                .Where(c => c.EmbeddingModel == model)
                .GroupBy(c => c.ContentHash, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new Queue<StoredChunk>(g), StringComparer.Ordinal);

            foreach (var chunk in ArticleChunker.Chunk(article.BodyMarkdown))
            {
                if (reusable.TryGetValue(chunk.ContentHash, out var rows) && rows.TryDequeue(out var row))
                {
                    keptIds.Add(row.Id);
                    if (row.Ordinal != chunk.Ordinal)
                    {
                        moved.Add(new MovedChunk(row.Id, chunk.Ordinal));
                    }
                }
                else
                {
                    toEmbed.Add((article.Id, chunk));
                }
            }
        }

        // Also removes the chunks of articles that no longer exist
        var deleted = stored.SelectMany(g => g).Select(c => c.Id).Where(id => !keptIds.Contains(id)).ToList();
        var kept = keptIds.Count;

        var added = await EmbedAsync(toEmbed, model, cancellationToken);
        var changes = new ChunkChanges(added, moved, deleted);
        if (!changes.IsEmpty)
        {
            await store.SaveAsync(changes, cancellationToken);
        }

        LogSynced(added.Count, kept, deleted.Count, model);
        return new EmbeddingSyncResult(added.Count, kept, deleted.Count);
    }

    private async Task<List<NewChunk>> EmbedAsync(List<(int ArticleId, ArticleChunkDraft Chunk)> chunks, string model, CancellationToken cancellationToken)
    {
        var dimensions = aiOptions.Value.Embeddings.Dimensions;
        var added = new List<NewChunk>(chunks.Count);
        foreach (var batch in chunks.Chunk(retrievalOptions.Value.EmbeddingBatchSize))
        {
            IReadOnlyList<ReadOnlyMemory<float>> vectors;
            try
            {
                vectors = await CheckedEmbeddings.GenerateAsync(generator, [.. batch.Select(c => c.Chunk.EmbeddingInput)], model, dimensions, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                // Most often Ollama isn't running or the model isn't pulled; say how to get past it
                throw new InvalidOperationException(
                    $"Couldn't embed the article chunks with {aiOptions.Value.Embeddings.Provider} '{model}' at startup ({exception.Message}). " +
                    "Start the embedding provider (for Ollama, pull the model first) or set AI__Embeddings__Provider=Fake.",
                    exception);
            }

            added.AddRange(batch.Select((c, i) => new NewChunk(c.ArticleId, c.Chunk, model, vectors[i])));
        }

        return added;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Article embeddings synced with {Model}: {Embedded} chunks embedded, {Kept} unchanged, {Deleted} deleted")]
    private partial void LogSynced(int embedded, int kept, int deleted, string model);
}

public sealed record EmbeddingSyncResult(int Embedded, int Kept, int Deleted);
