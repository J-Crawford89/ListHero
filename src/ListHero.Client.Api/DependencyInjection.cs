using ListHero.Client.Abstractions.Api;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Client.Api;

public static class DependencyInjection
{
    public static IServiceCollection AddListHeroApiClient(this IServiceCollection services, Uri baseAddress)
    {
        if (!baseAddress.IsAbsoluteUri || baseAddress.Scheme is not ("https" or "http"))
            throw new ArgumentException("The API address must be an absolute HTTP or HTTPS URL.", nameof(baseAddress));
        if (!baseAddress.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("The API address must end with a slash.", nameof(baseAddress));
        services.AddHttpClient<IAppStatusApi, AppStatusApiClient>(http =>
        {
            http.BaseAddress = baseAddress;
            http.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddHttpClient<IWishListApi, WishListApiClient>(http =>
        {
            http.BaseAddress = baseAddress;
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<IListCollaborationApi, ListCollaborationApiClient>(http =>
        {
            http.BaseAddress = baseAddress;
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        return services;
    }
}
