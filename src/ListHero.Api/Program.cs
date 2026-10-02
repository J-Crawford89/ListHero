using ListHero.Api.Authentication;
using ListHero.Api.Endpoints;
using ListHero.Api.Errors;
using ListHero.Application;
using ListHero.Contracts.Identity;
using ListHero.Contracts.System;
using ListHero.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);
var authenticationEnabled = builder.Configuration.GetValue<bool>("Authentication:Enabled");
if (!authenticationEnabled && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Configure Entra External ID before starting outside Development.");

if (authenticationEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("EntraExternalId"));
    builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
        options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.RoleClaimType = "roles";
        });
}
else
{
    builder.Services.AddAuthentication(UnconfiguredAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, UnconfiguredAuthenticationHandler>(
            UnconfiguredAuthenticationHandler.SchemeName, _ => { });
}

var requiredScope = builder.Configuration["Authentication:RequiredScope"] ?? "access_as_user";
builder.Services.AddAuthorization(options =>
{
    // Standard privileges do not require an explicitly assigned User role.
    var userPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
        .RequireAssertion(context => context.User.FindAll("scp")
            .Any(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(requiredScope)))
        .Build();
    options.DefaultPolicy = userPolicy;
    options.FallbackPolicy = userPolicy;
    options.AddPolicy(AppRoles.User, userPolicy);
    options.AddPolicy(AppRoles.Admin, new AuthorizationPolicyBuilder(userPolicy)
        .RequireRole(AppRoles.Admin).Build());
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApplicationExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddListHeroApplication();
var connectionString = builder.Configuration.GetConnectionString("ListHero")
    ?? throw new InvalidOperationException("A ListHero SQL Server connection string is required.");
builder.Services.AddListHeroInfrastructure(connectionString);

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/lists") || context.Request.Path.StartsWithSegments("/api/session"))
        context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();
app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapGet("/api/status", () => new AppStatusResponse("List Hero", "available"))
    .AllowAnonymous().WithName("GetAppStatus");
app.MapGet("/api/session", (HttpContext context) => new UserSessionResponse(
    context.User.GetObjectId() ?? context.User.FindFirst("sub")?.Value ?? string.Empty,
    context.User.Identity?.Name, context.User.IsInRole(AppRoles.Admin)))
    .RequireAuthorization(AppRoles.User).WithName("GetUserSession");
app.MapWishListEndpoints();
app.MapCollaborationEndpoints();
app.Run();

public partial class Program;
