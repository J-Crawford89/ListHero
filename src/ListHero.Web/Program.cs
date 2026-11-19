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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.AddOperationalDiagnostics();
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
    builder.Services.ObserveDistributedTokenCache();
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("EntraExternalId"))
        .EnableTokenAcquisitionToCallDownstreamApi(scopes)
        .AddDistributedTokenCaches();
    builder.Services.Configure<MsalDistributedTokenCacheAdapterOptions>(options => options.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(30));
    builder.Services.PostConfigure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, SessionPolicy.Configure);
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
            options.UseTokenLifetime = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "roles";
        });
    builder.Services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, AuthenticationDiagnostics.Configure);
}
else
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options => options.LoginPath = "/");
    builder.Services.AddScoped<IApiAccessTokenProvider, UnconfiguredApiAccessTokenProvider>();
}
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHostedDataProtection(builder.Configuration, "ListHero.Web");
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 256 * 1024);
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, DiagnosticCircuitHandler>();
builder.Services.AddListHeroClient();
builder.Services.AddListHeroApiClient(new Uri(builder.Configuration["ListHeroApi:BaseUrl"]
    ?? throw new InvalidOperationException("ListHeroApi:BaseUrl is required.")));
builder.Services.AddScoped<ProtectedLocalStorage>();
builder.Services.AddScoped<IGuestCredentialStore, BrowserGuestCredentialStore>();
builder.Services.AddScoped<IDraftRecoveryStore, BrowserDraftRecoveryStore>();
builder.Services.AddScoped<IHostPresentation, WebHostPresentation>();
builder.Services.AddScoped<IListShareUrlBuilder>(services => new ListShareUrlBuilder(new Uri(
    builder.Configuration["Sharing:PublicWebBaseUrl"] ?? services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().BaseUri)));

var app = builder.Build();
app.UseOperationalDiagnostics("Web");
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" && context.User.Identity?.IsAuthenticated == true)
    { context.Response.Headers.CacheControl = "no-store"; context.Response.Redirect("/dashboard"); return; }
    await next(context);
});
app.UseAuthorization();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (context.Request.Path == "/" || context.Request.Path.StartsWithSegments("/account") || context.Request.Path == "/sign-in" || context.Request.Path == "/welcome") context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/lists") || context.Request.Path.StartsWithSegments("/view") || context.Request.Path.StartsWithSegments("/dashboard") || context.Request.Path.StartsWithSegments("/saved")
        || context.Request.Path.StartsWithSegments("/discover") || context.Request.Path.StartsWithSegments("/people")
        || context.Request.Path.StartsWithSegments("/profile"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
if (authenticationEnabled)
{
    app.MapControllers();
    app.MapGet("/account/sign-in", (Guid? listId, string? returnUrl, string? rememberMe, HttpContext context) => Results.Challenge(
        new AuthenticationProperties { IsPersistent = rememberMe == "true", RedirectUri = context.Request.Cookies.ContainsKey("ListHero.PendingReturn") ? "/account/resume" : listId.HasValue ? $"/view/{listId}" : SessionPolicy.SafeReturn(returnUrl) },
        [OpenIdConnectDefaults.AuthenticationScheme])).AllowAnonymous();
    app.MapPost("/account/pending-return", async (HttpContext context, IAntiforgery antiforgery, IDataProtectionProvider protection) =>
    {
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { AuthenticationDiagnostics.SessionEvent(context, "pending-return", "antiforgery-rejected"); return Results.BadRequest(); }
        var form = await context.Request.ReadFormAsync();
        var destination = form["destination"].ToString(); var parts = destination.Split('#', 2);
        if (SessionPolicy.SafeReturn(parts[0]) != parts[0] || (parts.Length == 2 && (parts[1].Length > 128 || parts[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))))
        { AuthenticationDiagnostics.SessionEvent(context, "pending-return", "destination-rejected"); return Results.BadRequest(); }
        context.Response.Cookies.Append("ListHero.PendingReturn", protection.CreateProtector("pending-return-v1").ToTimeLimitedDataProtector().Protect(destination, TimeSpan.FromMinutes(10)), new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(10), Path = "/account" });
        AuthenticationDiagnostics.SessionEvent(context, "pending-return", "created");
        return Results.NoContent();
    }).AllowAnonymous();
    app.MapGet("/account/resume", (HttpContext context, IDataProtectionProvider protection) =>
    {
        var target = "/dashboard";
        if (context.Request.Cookies.TryGetValue("ListHero.PendingReturn", out var pending))
        { try { target = protection.CreateProtector("pending-return-v1").ToTimeLimitedDataProtector().Unprotect(pending); }
          catch (System.Security.Cryptography.CryptographicException) { AuthenticationDiagnostics.SessionEvent(context, "resume", "invalid-protected-context"); } }
        context.Response.Cookies.Delete("ListHero.PendingReturn", new CookieOptions { Path = "/account", Secure = true });
        AuthenticationDiagnostics.SessionEvent(context, "resume", "completed");
        return Results.Redirect(target);
    }).RequireAuthorization();
    app.MapPost("/account/session", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { AuthenticationDiagnostics.SessionEvent(context, "probe", "antiforgery-rejected"); return Results.BadRequest(); }
        var ticket = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Json(new { session = ticket.Principal?.FindFirst("ListHero.SessionId")?.Value });
    }).AllowAnonymous();
    app.MapPost("/account/sign-out", async (HttpContext context, IAntiforgery antiforgery) =>
    {
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { AuthenticationDiagnostics.SessionEvent(context, "sign-out", "antiforgery-rejected"); return (IResult)Results.BadRequest(); }
        context.Response.Cookies.Delete("ListHero.PendingReturn", new CookieOptions { Path = "/account", Secure = true });
        AuthenticationDiagnostics.SessionEvent(context, "sign-out", "requested");
        return Results.SignOut(
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
    }).RequireAuthorization();
}
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(ListHero.UI.Pages.Home).Assembly);
app.Run();
