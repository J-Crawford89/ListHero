using ListHero.Domain.Common;
using ListHero.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ListHero.Infrastructure.Persistence;

public sealed class ListHeroDbContext(DbContextOptions<ListHeroDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<WishList> WishLists => Set<WishList>();
    public DbSet<WishListItem> WishListItems => Set<WishListItem>();
    public DbSet<ListShareLink> ListShareLinks => Set<ListShareLink>();
    public DbSet<ItemPurchase> ItemPurchases => Set<ItemPurchase>();
    public DbSet<GuestIdentity> GuestIdentities => Set<GuestIdentity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureEntity<User>(modelBuilder, "Users");
        ConfigureEntity<WishList>(modelBuilder, "WishLists");
        ConfigureEntity<WishListItem>(modelBuilder, "WishListItems");
        ConfigureEntity<ListShareLink>(modelBuilder, "ListShareLinks");
        ConfigureEntity<ItemPurchase>(modelBuilder, "ItemPurchases");
        ConfigureEntity<GuestIdentity>(modelBuilder, "GuestIdentities");

        var user = modelBuilder.Entity<User>();
        user.Property(e => e.IdentityIssuer).HasMaxLength(200).IsRequired();
        user.Property(e => e.IdentitySubject).HasMaxLength(200).IsRequired();
        user.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
        user.HasIndex(e => new { e.IdentityIssuer, e.IdentitySubject }).IsUnique();

        var list = modelBuilder.Entity<WishList>();
        list.Property(e => e.Name).HasMaxLength(200).IsRequired();
        list.Property(e => e.Description).HasMaxLength(4000).IsRequired();
        list.Property(e => e.IsPublic).HasDefaultValue(false);
        list.Property(e => e.RowVersion).IsRowVersion();
        list.HasOne<User>().WithMany().HasForeignKey(e => e.OwnerId).OnDelete(DeleteBehavior.Restrict);
        list.HasIndex(e => new { e.OwnerId, e.ArchivedAt });

        var item = modelBuilder.Entity<WishListItem>();
        item.Property(e => e.Name).HasMaxLength(200).IsRequired();
        item.Property(e => e.Description).HasMaxLength(4000).IsRequired();
        item.Property(e => e.ApproximateUnitPrice).HasColumnType("float");
        item.Property(e => e.DesiredQuantity).HasDefaultValue(1);
        item.Property(e => e.Url).HasMaxLength(2048);
        item.Property(e => e.ImageUrl).HasMaxLength(2048);
        item.Property(e => e.RowVersion).IsRowVersion();
        item.HasOne<WishList>().WithMany().HasForeignKey(e => e.WishListId).OnDelete(DeleteBehavior.Restrict);
        item.HasIndex(e => new { e.WishListId, e.ArchivedAt, e.DisplayOrder });
        item.ToTable("WishListItems", table =>
        {
            table.HasCheckConstraint("CK_WishListItems_Quantity", "[DesiredQuantity] > 0");
            table.HasCheckConstraint("CK_WishListItems_Price", "[ApproximateUnitPrice] IS NULL OR [ApproximateUnitPrice] >= 0");
        });

        var share = modelBuilder.Entity<ListShareLink>();
        share.Property(e => e.TokenHash).HasMaxLength(64).IsUnicode(false).IsRequired();
        share.Property(e => e.ProtectedToken).HasMaxLength(2048).IsRequired();
        share.HasIndex(e => e.TokenHash).IsUnique();
        share.HasIndex(e => e.WishListId);
        share.HasOne<WishList>().WithMany().HasForeignKey(e => e.WishListId).OnDelete(DeleteBehavior.Restrict);

        var guest = modelBuilder.Entity<GuestIdentity>();
        guest.Property(e => e.CredentialHash).HasMaxLength(64).IsUnicode(false).IsRequired();
        guest.HasIndex(e => e.CredentialHash).IsUnique();

        var purchase = modelBuilder.Entity<ItemPurchase>();
        purchase.Ignore(e => e.IsActive);
        purchase.Property(e => e.RowVersion).IsRowVersion();
        purchase.HasOne<WishListItem>().WithMany().HasForeignKey(e => e.WishListItemId).OnDelete(DeleteBehavior.Restrict);
        purchase.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        purchase.HasOne<GuestIdentity>().WithMany().HasForeignKey(e => e.GuestIdentityId).OnDelete(DeleteBehavior.Restrict);
        purchase.HasIndex(e => new { e.WishListItemId, e.IdempotencyKey }).IsUnique();
        purchase.HasIndex(e => e.UserId);
        purchase.HasIndex(e => e.GuestIdentityId);
        purchase.ToTable("ItemPurchases", table =>
        {
            table.HasCheckConstraint("CK_ItemPurchases_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_ItemPurchases_Actor", "([UserId] IS NOT NULL AND [GuestIdentityId] IS NULL) OR ([UserId] IS NULL AND [GuestIdentityId] IS NOT NULL)");
        });
    }

    private static void ConfigureEntity<T>(ModelBuilder builder, string table) where T : Entity
    {
        var entity = builder.Entity<T>();
        entity.ToTable(table);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).ValueGeneratedNever();
        entity.Ignore(e => e.IsArchived);
        // Filtering is explicit: archived records must remain accessible to maintenance workflows.
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectPhysicalDeletes();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RejectPhysicalDeletes();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RejectPhysicalDeletes()
    {
        if (ChangeTracker.Entries<Entity>().Any(entry => entry.State == EntityState.Deleted))
            throw new InvalidOperationException("Archive List Hero records instead of physically deleting them.");
    }
}
