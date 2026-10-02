namespace ListHero.Application.Authorization;

// Construct only from validated authentication/guest credentials, never from a request's claimed IDs.
public sealed record ViewerIdentity(Guid? UserId = null, Guid? GuestIdentityId = null);

public sealed record ListPermissions(bool CanView, bool IsOwner, bool CanEdit,
    bool CanViewPurchases, bool CanMarkPurchases)
{
    public static ListPermissions Denied { get; } = new(false, false, false, false, false);
}
