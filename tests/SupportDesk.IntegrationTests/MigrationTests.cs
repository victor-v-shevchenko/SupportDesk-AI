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
}
