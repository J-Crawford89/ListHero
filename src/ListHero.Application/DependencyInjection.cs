using ListHero.Application.Authorization;
using ListHero.Application.Lists;
using ListHero.Application.Identity;
using ListHero.Application.Collaboration;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddListHeroApplication(this IServiceCollection services)
    {
        services.AddSingleton<IListAccessService, ListAccessService>();
        services.AddScoped<IWishListReadService, WishListReadService>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IWishListService, WishListService>();
        services.AddScoped<IListAccessResolver, ListAccessResolver>();
        services.AddScoped<IShareLinkService, ShareLinkService>();
        services.AddScoped<IListViewerService, ListViewerService>();
        services.AddScoped<IPurchaseService, PurchaseService>();
        return services;
    }
}
