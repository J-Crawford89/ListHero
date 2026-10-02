using ListHero.Domain.Entities;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Tests.Persistence;

public sealed class DatabaseModelTests
{
    [Fact]
    public void Sql_server_model_has_separate_tables_and_restricts_physical_cascades()
    {
        using var db = CreateContext();
        var entityTypes = db.Model.GetEntityTypes().ToArray();
        Assert.Equal(6, entityTypes.Length);
        Assert.Equal(6, entityTypes.Select(entity => entity.GetTableName()).Distinct().Count());
        Assert.All(entityTypes.SelectMany(entity => entity.GetForeignKeys()),
            foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        var purchase = db.Model.FindEntityType(typeof(ItemPurchase))!;
        Assert.Contains(purchase.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(["WishListItemId", "IdempotencyKey"]));
        Assert.True(purchase.FindProperty(nameof(ItemPurchase.RowVersion))!.IsConcurrencyToken);
    }

    [Fact]
    public async Task Physical_deletes_are_rejected_before_a_database_connection_is_used()
    {
        using var db = CreateContext();
        var item = new WishListItem(Guid.NewGuid(), "Keep history", "", DateTimeOffset.UtcNow);
        db.Attach(item);
        db.Remove(item);
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static ListHeroDbContext CreateContext() => new(new DbContextOptionsBuilder<ListHeroDbContext>()
        .UseSqlServer("Server=localhost;Database=ListHeroModelTests;Integrated Security=true").Options);
}
