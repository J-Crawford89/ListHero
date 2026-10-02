# List Hero

A consumer wish-list app built with .NET 10, a Blazor Web App using Interactive Server, an ASP.NET Core API, and EF Core for SQL Server. Shared client libraries and a Razor class library prepare the UI for a future .NET MAUI Blazor Hybrid host.

## Current status

Implemented:

- Nine application projects and one test project in `ListHero.slnx`.
- Shared landing page, layout, styles, and an interactive development connection check.
- Separate client orchestration and HTTP transport libraries, demonstrated by the API connection check.
- Six domain entities, SQL Server mappings, and an initial EF Core migration.
- List access policy, separate owner/viewer response projections, fulfillment and overpurchase calculations.
- Capability-token generation, hashing, encrypted share-token recovery, and a browser guest-credential storage adapter.
- Configurable Entra External ID web sign-in and API bearer validation, standard-user policy, and placeholder admin policy.
- Internal user provisioning from validated API issuer/object identity, scoped delegated API token acquisition, and authenticated HTTP transport.
- Protected owner endpoints and shared forms to create private lists, list/reopen owned lists, and add items with all agreed fields.
- Owner list/item editing, public/private visibility, removal confirmations, soft archival, and row-version conflict handling that preserves unsaved drafts.
- Owner share-link management with stable links, optional UTC expiration, and independent revocation; tokens travel in URL fragments and API headers.
- Public/private viewer pages, anonymous guest-credential issuance and persistence, idempotent purchase marks, own-mark undo, fulfillment/overpurchase indicators, and purchase-free owner responses.
- Conditional item-edit warnings and viewer refresh every 20 seconds.
- Tests for authorization, privacy, purchase ownership, overpurchasing, token handling, persistence metadata, and API startup/security.

The core workflows are implemented. Real customer sign-in and list/item creation have been verified against the List Hero external tenant and LocalDB. Automated tests cover the remaining owner and collaboration operations, including real SQL Server concurrency; an isolated browser check verified anonymous marking through a share link, persistence of guest ownership after reload, and undo. Live second-customer Entra isolation, sign-out, and token renewal checks remain pending. No database migration runs automatically at startup. See [core workflow guide](docs/core-workflows.md) for usage, verification, and current limits.

## Prerequisites

- .NET 10 SDK; `global.json` accepts the latest installed .NET 10 feature band starting at 10.0.401.
- SQL Server or SQL Server LocalDB to use the database. Running the shell and connection check does not require a database.
- Entra External ID external tenant and separate web/API app registrations to enable real authentication.
- A recent Visual Studio version with .NET 10 support, or another editor.

## Build and test

From the repository root:

```powershell
dotnet restore ListHero.slnx
dotnet tool restore
dotnet build ListHero.slnx --no-restore
dotnet test ListHero.slnx --no-build --no-restore
```

Package versions are centralized in `Directory.Packages.props`. NuGet sources are specified in `NuGet.Config`. Compiler warnings are treated as errors.

The suite contains 150 cases. The default run executes 144 and explicitly skips five SQL Server tests and one browser regression. To include the SQL tests, use a fresh database name; scenarios apply migrations to separate database suffixes and retain them for inspection:

```powershell
$testDatabase = 'ListHero_Integration_' + [guid]::NewGuid().ToString('N')
$env:LISTHERO_TEST_SQL_CONNECTION = "Server=(localdb)\MSSQLLocalDB;Database=$testDatabase;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test ListHero.slnx --no-build --no-restore
Remove-Item Env:LISTHERO_TEST_SQL_CONNECTION
```

Test identity injection exists only inside the test assembly and test-host configuration. The application does not accept test identity headers. SQL checks cover persistence, constraints, revocation/expiration, row versions, archival, undo, duplicate-request races, and concurrency recovery. Controlled signed-token tests exercise actual JWT middleware; live tenant acceptance checks remain separate.

See [test coverage and full-suite instructions](docs/test-coverage.md) to run the headless browser regression, collect coverage, and enforce the per-assembly minimums. CI includes a Windows job with SQL and Chromium and requires every test to execute. Coverage excludes generated build output and migration/model metadata while retaining application and Razor component code.

## Run locally

For HTTPS, trust the .NET development certificate if it is not already trusted:

```powershell
dotnet dev-certs https --trust
```

Start the API and web app in separate terminals:

Alternatively, build Release and run `scripts/Start-Local.ps1` to start both hosts in the background. `scripts/Stop-Local.ps1` stops only the recorded processes after verifying their executable paths and start times. Logs and process records stay under Git-ignored `.artifacts`.

```powershell
dotnet run --project src/ListHero.Api --launch-profile https
```

```powershell
dotnet run --project src/ListHero.Web --launch-profile https
```

- Web: <https://localhost:7016>
- Development connection check: <https://localhost:7016/development/status>
- API status: <https://localhost:7223/api/status>
- Development OpenAPI document: <https://localhost:7223/openapi/v1.json>
- API liveness: <https://localhost:7223/health/live>

The connection check verifies the reusable UI/client/API path. API status and liveness do not assert database connectivity or Entra configuration.

For a local HTTP-only shell preview, run the API with `--launch-profile http`, and configure the web host before running it:

```powershell
$env:ListHeroApi__BaseUrl = 'http://localhost:5076/'
dotnet run --project src/ListHero.Web --launch-profile http
```

Web HTTP uses port 5113. Use HTTPS for real sign-in and guest credentials.

## SQL Server

The development default is `(localdb)\MSSQLLocalDB`, database `ListHero`, Windows authentication. Override `ConnectionStrings:ListHero` with user secrets or the `ConnectionStrings__ListHero` environment variable for another server.

The EF design-time factory reads `ConnectionStrings__ListHero`; it otherwise uses the LocalDB default. It does not read web/API user secrets. When applying a migration to another server, set that environment variable or use the EF CLI's `--connection` option.

To apply the initial migration to your chosen local database:

```powershell
dotnet ef database update --project src/ListHero.Infrastructure --startup-project src/ListHero.Api
```

To generate a reviewable SQL script without connecting to a database:

```powershell
dotnet ef migrations script --idempotent --project src/ListHero.Infrastructure --startup-project src/ListHero.Api --output initial-schema.sql
```

Do not run the production application as the migration administrator. Deployment should apply reviewed migrations separately.

## Entra External ID

Authentication is disabled by default **only to let Development run without tenant configuration**. The API still returns 401 for protected routes; there is no fake user or authorization bypass. Both hosts refuse to start outside Development until authentication is enabled.

The selected List Hero tenant's registrations are configured; see [completed identity setup](docs/azure-setup.md). On this development account, user secrets enable authentication and hold the web credential. The following checklist describes setup for another environment:

1. Configure a sign-up/sign-in user flow and associate the web application with it.
2. Give the web registration the redirect URI `https://localhost:7016/signin-oidc` and the signed-out callback URI `https://localhost:7016/signout-callback-oidc` as appropriate for your registration.
3. Expose the delegated API scope `access_as_user`, set the API registration's requested access-token version to 2, and grant the web application access to it with tenant admin consent. Configure web `ListHeroApi:Scopes:0` as `api://YOUR-API-CLIENT-ID/access_as_user`.
4. Define an API application role with value `Admin` and assign it explicitly to administrators. Normal users do not require a role assignment.
5. Configure both hosts' `EntraExternalId:Instance`, `TenantId`, and their respective `ClientId` values; enable `Authentication:Enabled`.
6. Set the web registration's client secret through user secrets for local development. Use an appropriate secret/certificate mechanism for deployment.

Example commands use placeholders, not real credentials:

```powershell
dotnet user-secrets set 'EntraExternalId:Instance' 'https://YOUR-TENANT-SUBDOMAIN.ciamlogin.com/' --project src/ListHero.Web
dotnet user-secrets set 'EntraExternalId:TenantId' 'YOUR-TENANT-ID' --project src/ListHero.Web
dotnet user-secrets set 'EntraExternalId:ClientId' 'YOUR-WEB-CLIENT-ID' --project src/ListHero.Web
dotnet user-secrets set 'EntraExternalId:ClientSecret' 'YOUR-WEB-CLIENT-SECRET' --project src/ListHero.Web
dotnet user-secrets set 'ListHeroApi:Scopes:0' 'api://YOUR-API-CLIENT-ID/access_as_user' --project src/ListHero.Web
dotnet user-secrets set 'Authentication:Enabled' 'true' --project src/ListHero.Web
```

Configure the API's Instance, TenantId, ClientId, and Enabled values the same way with `--project src/ListHero.Api`; an API validating incoming tokens does not need the web application's secret.

Once configured, sign in from the home page to reach `/lists`. Create a list, add items, return to My lists, and reopen it. The web host acquires an API token for the circuit's explicit authenticated principal and sends it on each protected request. MSAL handles cached tokens/refresh; interactive reauthentication is offered when required. Internal users are provisioned by the API from the validated issuer and object ID, never email or an unverified request ID. Owners receive only the purchase-free owner response type.

The current distributed-cache implementation is process memory, suitable for local development. Configure persistent shared token caching before scaling the web host; a restart currently requires signing in again when the cached token is lost.

Microsoft references:

- [External ID overview](https://learn.microsoft.com/en-us/entra/external-id/external-identities-overview)
- [Configure web authentication](https://learn.microsoft.com/en-us/entra/identity-platform/tutorial-web-app-dotnet-sign-in-users)
- [Blazor with Microsoft Entra ID](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/blazor-web-app-with-entra?view=aspnetcore-10.0)

## Deployment and future hosts

Azure App Service and Azure SQL are the initial hosting direction; deployment resources are not provisioned by this scaffold. Persist and protect ASP.NET Core Data Protection keys for both hosts before deploying or scaling. API keys protect recoverable share tokens; web keys protect login cookies and persisted guest credentials. Losing the key ring can make those existing values unreadable. Multiple instances of the same host must share its key ring and application name.

The MAUI host is deliberately deferred. It will reference UI, Client, Client.Api, and Contracts, provide device storage/authentication adapters, and continue to use the same API. Domain, Application, and Infrastructure belong to the backend.

See [architecture and accepted product rules](docs/architecture.md) for project ownership and the next implementation sequence.

See [Azure and identity setup](docs/azure-setup.md) for the information needed to configure your tenant and the development free-tier plan.
