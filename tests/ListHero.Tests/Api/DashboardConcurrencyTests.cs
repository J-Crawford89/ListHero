using ListHero.Application.Collaboration;
using ListHero.Client.Api;
using ListHero.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ListHero.Tests.Api;

public sealed partial class OwnerWorkflowTests
{
    [SqlServerFact]
    public async Task Pin_request_using_an_older_saved_grant_cannot_overwrite_a_new_share_link()
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausePin = false;
        await using var factory = new OwnerApiFactory(IsolatedSqlConnection("_PinGrantRace"), overrides: services =>
        {
            services.RemoveAll<IListAccessResolver>();
            services.AddScoped<IListAccessResolver>(provider => new AccessResolvedHook(
                ActivatorUtilities.CreateInstance<ListAccessResolver>(provider), async request =>
                {
                    if (!pausePin || request.ShareToken is not null) return;
                    paused.TrySetResult();
                    await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }));
        });
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ListHeroDbContext>().Database.MigrateAsync();
        using var owner = SignedInClient(factory);
        using var buyer = SignedInClient(factory);
        var list = await CreateListAsync(owner);
        var links = new ListCollaborationApiClient(owner, new TestTokenProvider());
        var original = await links.CreateLinkAsync(list.Id, new());
        var replacement = await links.CreateLinkAsync(list.Id, new());
        var saved = new DashboardApiClient(buyer, new TestTokenProvider());
        await saved.SaveAsync(list.Id, null, original.Token);
        pausePin = true;
        var pin = saved.SaveAsync(list.Id, true);
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await saved.SaveAsync(list.Id, null, replacement.Token);
        }
        finally { resume.TrySetResult(); }
        await pin;
        using var verify = factory.Services.CreateScope();
        var entry = await verify.ServiceProvider.GetRequiredService<ListHeroDbContext>().SavedLists.AsNoTracking().SingleAsync();
        Assert.True(entry.IsPinned);
        Assert.Equal(replacement.Id, entry.ShareLinkId);
        await links.RevokeLinkAsync(list.Id, original.Id);
        Assert.True(Assert.Single((await saved.SavedAsync()).Items).IsAvailable);
    }

    private sealed class AccessResolvedHook(IListAccessResolver inner, Func<ListRequestAccess, Task> afterResolve) : IListAccessResolver
    {
        public async Task<ResolvedListAccess> ResolveAsync(Guid listId, ListRequestAccess request, CancellationToken ct = default)
        {
            var resolved = await inner.ResolveAsync(listId, request, ct);
            await afterResolve(request);
            return resolved;
        }
    }
}
