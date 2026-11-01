using Microsoft.EntityFrameworkCore;

namespace SupportDesk.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_apply_and_enable_the_vector_extension()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDatabase.CreateDbContext(postgres.ConnectionString);

        await db.Database.MigrateAsync(ct);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
        var extensions = await db.Database
            .SqlQueryRaw<string>("SELECT extname AS \"Value\" FROM pg_extension WHERE extname = 'vector'")
            .ToListAsync(ct);
        Assert.Equal(["vector"], extensions);
    }

    [Fact]
    public async Task Chunk_embeddings_have_an_hnsw_index_for_cosine_distance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = TestDatabase.CreateDbContext(postgres.ConnectionString);
        await db.Database.MigrateAsync(ct);

        var definition = await db.Database
            .SqlQueryRaw<string>("SELECT indexdef AS \"Value\" FROM pg_indexes WHERE indexname = 'IX_ArticleChunks_Embedding'")
            .SingleAsync(ct);
        var column = await db.Database
            .SqlQueryRaw<string>("SELECT format_type(atttypid, atttypmod) AS \"Value\" FROM pg_attribute WHERE attrelid = '\"ArticleChunks\"'::regclass AND attname = 'Embedding'")
            .SingleAsync(ct);

        Assert.Contains("USING hnsw", definition, StringComparison.Ordinal);
        Assert.Contains("vector_cosine_ops", definition, StringComparison.Ordinal);
        Assert.Equal("vector(1024)", column);
    }
}
