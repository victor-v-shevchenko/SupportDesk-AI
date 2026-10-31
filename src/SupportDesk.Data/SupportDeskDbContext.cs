using Microsoft.EntityFrameworkCore;
using SupportDesk.Core.Articles;
using SupportDesk.Core.Tickets;

namespace SupportDesk.Data;

public class SupportDeskDbContext(DbContextOptions<SupportDeskDbContext> options) : DbContext(options)
{
    /// <summary>Feeds ticket numbers (<c>T-1001</c>, ...); the seeder moves it past the seeded numbers.</summary>
    public const string TicketNumberSequence = "ticket_numbers";

    public const int TicketNumberStart = 1001;

    public DbSet<Ticket> Tickets => Set<Ticket>();

    public DbSet<Article> Articles => Set<Article>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // pgvector, for article chunk embeddings (step 4)
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
    }
}
