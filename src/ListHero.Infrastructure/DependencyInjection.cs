using ListHero.Application.Security;
using ListHero.Application.Identity;
using ListHero.Application.Lists;
using ListHero.Application.Collaboration;
using ListHero.Infrastructure.Persistence;
using ListHero.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ListHero.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddListHeroInfrastructure(this IServiceCollection services, string connectionString, bool enableRetryOnFailure = false)
    {
        services.AddDbContext<ListHeroDbContext>(options => options.UseSqlServer(connectionString, sql =>
        {
            if (enableRetryOnFailure) sql.EnableRetryOnFailure(6, TimeSpan.FromSeconds(10), null);
        }));
        services.AddDataProtection().SetApplicationName("ListHero.Api");
        services.AddSingleton<ICapabilityTokenService, CapabilityTokenService>();
        services.AddScoped<IUserStore, EfUserStore>();
        services.AddScoped<IWishListStore, EfWishListStore>();
        services.AddScoped<EfCollaborationStore>();
        services.AddScoped<IListViewerStore>(provider => provider.GetRequiredService<EfCollaborationStore>());
        services.AddScoped<IShareLinkStore>(provider => provider.GetRequiredService<EfCollaborationStore>());
        services.AddScoped<IPurchaseStore>(provider => provider.GetRequiredService<EfCollaborationStore>());
        return services;
    }
}
