using ListHero.Client.Abstractions.Api;
using Microsoft.AspNetCore.Components;

namespace ListHero.UI.Components;

public abstract class OwnerPageBase : RequestPageBase
{
    [Inject] protected IWishListApi Api { get; set; } = default!;
}
