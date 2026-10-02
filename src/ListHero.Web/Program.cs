using ListHero.Client;
using ListHero.Client.Abstractions;
using ListHero.Client.Abstractions.Api;
using ListHero.Client.Api;
using ListHero.Client.Services;
using ListHero.Web.Components;
using ListHero.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using Microsoft.Identity.Web.TokenCacheProviders.Distributed;
using ListHero.Hosting;

var builder = WebApplication.CreateBuilder(args);
var authenticationEnabled = builder.Configuration.GetValue<bool>("Authentication:Enabled");
if (!authenticationEnabled && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Configure Entra External ID before starting outside Development.");
if (authenticationEnabled)
{
    var scopes = builder.Configuration.GetSection("ListHeroApi:Scopes").Get<string[]>();
    if (scopes is not { Length: > 0 } || scopes.Any(string.IsNullOrWhiteSpace))
        throw new InvalidOperationException("Configure ListHeroApi:Scopes before enabling authentication.");
    if (builder.Configuration.GetConnectionString("TokenCache") is { Length: > 0 } tokenCacheConnection)
    {
        builder.Services.AddDistributedSqlServerCache(options =>
        {
            options.ConnectionString = tokenCacheConnection;
            options.SchemaName = "cache";
            options.TableName = "TokenCache";
        });
        builder.Services.Configure<MsalDistributedTokenCacheAdapterOptions>(options => options.Encrypt = true);
    }
    else
    {
        if (builder.Configuration.GetValue<bool>("DataProtection:RequirePersistentKeys"))
            throw new InvalidOperationException("Hosted authentication requires the persistent token cache connection.");
        builder.Services.AddDistributedMemoryCache();
    }
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("EntraExternalId"))
        .EnableTokenAcquisitionToCallDownstreamApi(scopes)
        .AddDistributedTokenCaches();
    // A separate cookie isolates browser tests hosted on another localhost port.
    // Browsers otherwise share host cookies across ports. Normal sessions retain their cookie.
    if (builder.Environment.IsDevelopment() && builder.Configuration["Development:CookieName"] is { Length: > 0 } cookieName)
        builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme,
            options => options.Cookie.Name = cookieName);
    builder.Services.AddScoped<IApiAccessTokenProvider, WebApiAccessTokenProvider>();
    builder.Services.AddControllersWithViews().AddMicrosoftIdentityUI();
    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme,
        options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "roles";
        });
}
else
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options => options.LoginPath = "/");
    builder.Services.AddScoped<IApiAccessTokenProvider, UnconfiguredApiAccessTokenProvider>();
}
builder.Services.AddAuthorization();
builder.Services.AddHostedDataProtection(builder.Configuration, "ListHero.Web");
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddListHeroClient();
builder.Services.AddListHeroApiClient(new Uri(builder.Configuration["ListHeroApi:BaseUrl"]
    ?? throw new InvalidOperationException("ListHeroApi:BaseUrl is required.")));
builder.Services.AddScoped<ProtectedLocalStorage>();
builder.Services.AddScoped<IGuestCredentialStore, BrowserGuestCredentialStore>();
builder.Services.AddScoped<IHostPresentation, WebHostPresentation>();
builder.Services.AddScoped<IListShareUrlBuilder>(services => new ListShareUrlBuilder(new Uri(
    builder.Configuration["Sharing:PublicWebBaseUrl"] ?? services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().BaseUri)));

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path.StartsWithSegments("/lists") || context.Request.Path.StartsWithSegments("/view"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
if (authenticationEnabled)
{
    app.MapControllers();
    app.MapGet("/account/sign-in", (Guid? listId) => Results.Challenge(
        new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = listId.HasValue ? $"/view/{listId}" : "/lists" },
        [OpenIdConnectDefaults.AuthenticationScheme])).AllowAnonymous();
    app.MapPost("/account/sign-out", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return (IResult)Results.BadRequest(); }
        return Results.SignOut(
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
    }).RequireAuthorization();
}
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(ListHero.UI.Pages.Home).Assembly);
app.Run();
