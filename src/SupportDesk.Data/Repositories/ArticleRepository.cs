using Microsoft.EntityFrameworkCore;
using SupportDesk.Core.Articles;

namespace SupportDesk.Data.Repositories;

internal sealed class ArticleRepository(SupportDeskDbContext db) : IArticleRepository
{
    public async Task<IReadOnlyList<Article>> ListAsync(CancellationToken cancellationToken) =>
        await db.Articles.AsNoTracking().OrderBy(a => a.Title).ToListAsync(cancellationToken);

    public Task<Article?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
        db.Articles.AsNoTracking().FirstOrDefaultAsync(a => a.Slug == slug, cancellationToken);
}
