using ListHero.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;

namespace ListHero.Tests.Security;

public sealed class CapabilityTokenTests
{
    [Fact]
    public void Capabilities_match_only_their_hash_and_protected_links_can_be_recovered()
    {
        var service = new CapabilityTokenService(new EphemeralDataProtectionProvider());
        var issued = service.Issue();
        var other = service.Issue();
        Assert.True(service.Matches(issued.Token, issued.Hash));
        Assert.False(service.Matches(other.Token, issued.Hash));
        Assert.False(service.Matches(issued.Token, new string('Z', 64)));
        Assert.False(service.Matches("", issued.Hash));
        var encrypted = service.ProtectShareToken(issued.Token);
        Assert.DoesNotContain(issued.Token, encrypted);
        Assert.Equal(issued.Token, service.UnprotectShareToken(encrypted));
    }
}
