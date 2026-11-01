using Microsoft.EntityFrameworkCore;
using SupportDesk.Core.AI.Usage;
using SupportDesk.Core.Articles;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Data;

public class SupportDeskDbContext(DbContextOptions<SupportDeskDbContext> options) : DbContext(options)
{
    /// <summary>Feeds ticket numbers (<c>T-1001</c>, ...); the seeder moves it past the seeded numbers.</summary>
    public const string TicketNumberSequence = "ticket_numbers";

    public const int TicketNumberStart = 1001;

    /// <summary>The size of <c>ArticleChunks.Embedding</c>; <c>AI:Embeddings:Dimensions</c> must match it.</summary>
    public const int EmbeddingDimensions = 1024;

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<Article> Articles => Set<Article>();

    public DbSet<ModelCall> ModelCalls => Set<ModelCall>();

    public DbSet<ArticleChunk> ArticleChunks => Set<ArticleChunk>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // pgvector, for article chunk embeddings
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.HasSequence<long>(TicketNumberSequence).StartsAt(TicketNumberStart);

        modelBuilder.Entity<Ticket>(ticket =>
        {
            ticket.Property(t => t.Number).HasMaxLength(20);
            ticket.HasIndex(t => t.Number).IsUnique();
            ticket.Property(t => t.Subject).HasMaxLength(Ticket.SubjectMaxLength);
            ticket.Property(t => t.Body).HasMaxLength(Ticket.BodyMaxLength);
            ticket.Property(t => t.CustomerName).HasMaxLength(Ticket.CustomerNameMaxLength);
            ticket.Property(t => t.OrderNumber).HasMaxLength(OrderNumber.Length);
            ticket.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            ticket.HasIndex(t => t.CreatedAt);

            ticket.OwnsOne(t => t.Summary, summary =>
            {
                summary.Property(s => s.Text).HasColumnName("SummaryText");
                summary.Property(s => s.PromptVersion).HasColumnName("SummaryPromptVersion").HasMaxLength(50);
                summary.Property(s => s.GeneratedAt).HasColumnName("SummaryGeneratedAt");
            });

            ticket.OwnsOne(t => t.Classification, classification =>
            {
                classification.Property(c => c.Category).HasColumnName("Category").HasConversion<string>().HasMaxLength(20);
                classification.Property(c => c.Priority).HasColumnName("Priority").HasConversion<string>().HasMaxLength(20);
                classification.Property(c => c.Reason).HasColumnName("ClassificationReason").HasMaxLength(200);
                classification.Property(c => c.Status).HasColumnName("ClassificationStatus").HasConversion<string>().HasMaxLength(20);
                classification.Property(c => c.PromptVersion).HasColumnName("ClassificationPromptVersion").HasMaxLength(50);
                classification.Property(c => c.ClassifiedAt).HasColumnName("ClassifiedAt");
            });
            ticket.Navigation(t => t.Classification).IsRequired();
        });

        modelBuilder.Entity<Article>(article =>
        {
            article.Property(a => a.Slug).HasMaxLength(Article.SlugMaxLength);
            article.HasIndex(a => a.Slug).IsUnique();
            article.Property(a => a.Title).HasMaxLength(Article.TitleMaxLength);
        });

        modelBuilder.Entity<ModelCall>(call =>
        {
            call.Property(c => c.Feature).HasConversion<string>().HasMaxLength(20);
            call.Property(c => c.Provider).HasMaxLength(ModelCall.ProviderMaxLength);
            call.Property(c => c.Model).HasMaxLength(ModelCall.ModelMaxLength);
            call.Property(c => c.PromptVersion).HasMaxLength(ModelCall.PromptVersionMaxLength);
            call.Property(c => c.CostUsd).HasPrecision(12, 6);
            call.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(20);
            // No foreign key to Tickets: a usage row must never fail to save because of the ticket
            call.HasIndex(c => new { c.TicketId, c.Timestamp });
            call.HasIndex(c => c.Timestamp);
        });

        modelBuilder.Entity<ArticleChunk>(chunk =>
        {
            chunk.HasOne(c => c.Article).WithMany().HasForeignKey(c => c.ArticleId).OnDelete(DeleteBehavior.Cascade);
            chunk.HasIndex(c => new { c.ArticleId, c.Ordinal });
            chunk.Property(c => c.ContentHash).HasMaxLength(64).IsFixedLength();
            chunk.Property(c => c.EmbeddingModel).HasMaxLength(ArticleChunk.EmbeddingModelMaxLength);
            chunk.Property(c => c.Embedding).HasColumnType($"vector({EmbeddingDimensions})");
            // Cosine distance (<=>), the operator search orders by; an index built for another operator wouldn't be used
            chunk.HasIndex(c => c.Embedding).HasMethod("hnsw").HasOperators("vector_cosine_ops");
        });
    }
}
