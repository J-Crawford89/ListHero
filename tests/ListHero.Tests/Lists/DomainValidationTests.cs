using ListHero.Domain.Entities;

namespace ListHero.Tests.Lists;

public sealed class DomainValidationTests
{
    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.NegativeInfinity)] [InlineData(-0.01)]
    public void Invalid_estimates_cannot_mutate_an_existing_item(double price)
    {
        var item = new WishListItem(Guid.NewGuid(), "Original", "Description", DateTimeOffset.UtcNow, 20);
        Assert.Throws<ArgumentOutOfRangeException>(() => item.Update("Changed", "", price, 1, 0, 0, null, null));
        Assert.Equal("Original", item.Name); Assert.Equal(20, item.ApproximateUnitPrice);
    }
    [Theory]
    [InlineData("name")] [InlineData("description")] [InlineData("url")]
    public void Oversized_fields_are_rejected_before_an_existing_item_is_mutated(string field)
    {
        var item = new WishListItem(Guid.NewGuid(), "Original", "Description", DateTimeOffset.UtcNow);
        Assert.ThrowsAny<ArgumentException>(() => item.Update(field == "name" ? new('a', 201) : "Changed",
            field == "description" ? new('a', 4001) : "", null, 1, 0, 0,
            field == "url" ? "https://example.test/" + new string('a', 2048) : null, null));
        Assert.Equal("Original", item.Name); Assert.Equal("Description", item.Description);
    }
    [Theory]
    [InlineData("neither")] [InlineData("both")] [InlineData("empty-user")] [InlineData("empty-guest")]
    public void A_purchase_actor_must_be_exactly_one_nonempty_user_or_guest(string actor)
    {
        Guid? user = actor is "both" ? Guid.NewGuid() : actor is "empty-user" ? Guid.Empty : null;
        Guid? guest = actor is "both" ? Guid.NewGuid() : actor is "empty-guest" ? Guid.Empty : null;
        Assert.Throws<ArgumentException>(() => new ItemPurchase(Guid.NewGuid(), 1, user, guest, Guid.NewGuid(), DateTimeOffset.UtcNow));
    }
}
