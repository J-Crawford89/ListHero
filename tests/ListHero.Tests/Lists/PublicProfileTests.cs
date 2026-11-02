using ListHero.Application.Discovery;
using ListHero.Contracts.Discovery;
using ListHero.Domain.Entities;

namespace ListHero.Tests.Lists;

public sealed class PublicProfileTests
{
    [Theory]
    [InlineData("ab")] [InlineData("_abc")] [InlineData("-abc")] [InlineData("has space")]
    [InlineData("éclair")] [InlineData("@@abc")] [InlineData("a/b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz12345")]
    public void Invalid_handles_are_rejected(string handle) => Assert.Throws<ArgumentException>(() => User.NormalizeHandle(handle));

    [Fact]
    public void Public_identity_is_explicit_and_does_not_overwrite_login_identity()
    {
        var user = new User("issuer", "subject", "Private identity name", DateTimeOffset.UtcNow);
        Assert.False(user.HasPublicProfile);
        user.UpdatePublicProfile(" Jane ", " @JANE_2 ");
        Assert.True(user.HasPublicProfile); Assert.Equal("Jane", user.PublicDisplayName); Assert.Equal("jane_2", user.PublicHandle);
        user.UpdatePublicProfile("New display name", "JANE_2");
        Assert.Throws<ArgumentException>(() => user.UpdatePublicProfile("Jane", "someone-else"));
        Assert.Throws<ArgumentException>(() => user.UpdatePublicProfile(" ", "jane_2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => user.UpdatePublicProfile(new string('x', 101), "jane_2"));
        Assert.Equal("Private identity name", user.DisplayName);
    }

    [Theory]
    [InlineData("x", 1, 20)] [InlineData("", 0, 20)] [InlineData("", 101, 20)] [InlineData("", 1, 0)] [InlineData("", 1, 51)]
    [InlineData(".....................................................................................................", 1, 20)]
    public async Task Discovery_rejects_unbounded_or_invalid_queries(string text, int page, int size)
        => await Assert.ThrowsAsync<ArgumentException>(() => new DiscoveryService(new NoReads()).GetListsAsync(new(text, page, size)));

    [Fact]
    public async Task Empty_people_search_does_not_enumerate_accounts()
        => Assert.Empty((await new DiscoveryService(new NoReads()).GetPeopleAsync(new("  "))).Items);

    private sealed class NoReads : IDiscoveryStore
    {
        public Task<DiscoveryPage<PublicListSummary>> GetListsAsync(DiscoveryQuery query, CancellationToken ct) => throw new InvalidOperationException("Must not query");
        public Task<DiscoveryPage<PublicPersonSummary>> GetPeopleAsync(DiscoveryQuery query, CancellationToken ct) => throw new InvalidOperationException("Must not query");
        public Task<PublicProfileResponse?> GetProfileAsync(string handle, DiscoveryQuery query, CancellationToken ct) => throw new InvalidOperationException("Must not query");
    }
}
