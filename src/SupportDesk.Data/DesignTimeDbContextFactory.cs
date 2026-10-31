using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SupportDesk.Data;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without a database or the API's configuration.</summary>
[ExcludeFromCodeCoverage(Justification = "Only used by the dotnet ef tool")]
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SupportDeskDbContext>
{
    public SupportDeskDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SupportDeskDbContext>().UseNpgsql("Host=localhost").Options);
}
