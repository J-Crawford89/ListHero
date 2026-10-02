using System.Net;
using System.Net.Http.Json;
using ListHero.Client.Api;
using ListHero.Client.Services;
using ListHero.Contracts.System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ListHero.Tests.Api;

public sealed class ApiSmokeTests
{
    [Fact]
    public async Task Client_service_reaches_the_API_using_the_real_HTTP_client_adapter()
    {
        await using var factory = new DevelopmentApiFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var service = new AppStatusService(new AppStatusApiClient(http));
        Assert.True((await service.CheckAsync()).IsAvailable);
        var response = await http.GetFromJsonAsync<AppStatusResponse>("/api/status");
        Assert.Equal("List Hero", response!.Application);
    }

    [Fact]
    public async Task Missing_tenant_configuration_never_grants_authenticated_access()
    {
        await using var factory = new DevelopmentApiFactory();
        using var http = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        http.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-valid-token");
        var response = await http.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Production_cannot_start_with_authentication_disabled()
    {
        using var factory = new ProductionApiFactory();
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Configure Entra External ID", exception.Message);
    }

    private sealed class DevelopmentApiFactory : WebApplicationFactory<ListHero.Api.ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.UseEnvironment("Development").UseSetting("Authentication:Enabled", "false");
    }

    private sealed class ProductionApiFactory : WebApplicationFactory<ListHero.Api.ApiAssemblyMarker>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.UseEnvironment("Production").UseSetting("Authentication:Enabled", "false");
    }
}
