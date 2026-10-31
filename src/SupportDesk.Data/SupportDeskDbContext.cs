using Microsoft.EntityFrameworkCore;

namespace SupportDesk.Data;

public class SupportDeskDbContext(DbContextOptions<SupportDeskDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // pgvector, for article chunk embeddings (step 4)
        modelBuilder.HasPostgresExtension("vector");
    }
}
