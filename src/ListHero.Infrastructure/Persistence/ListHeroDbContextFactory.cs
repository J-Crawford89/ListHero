using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ListHero.Infrastructure.Persistence;

public sealed class ListHeroDbContextFactory : IDesignTimeDbContextFactory<ListHeroDbContext>
{
    public ListHeroDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ListHero")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=ListHero;Integrated Security=true;TrustServerCertificate=true";
        return new(new DbContextOptionsBuilder<ListHeroDbContext>().UseSqlServer(connectionString).Options);
    }
}
