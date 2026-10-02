using ListHero.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ListHero.Client;

public static class DependencyInjection
{
    public static IServiceCollection AddListHeroClient(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAppStatusService, AppStatusService>();
        services.AddScoped<IListViewerClient, ListViewerClient>();
        return services;
    }
}
