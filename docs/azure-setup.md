# Azure and identity setup

The first milestone ran Web, API, and SQL Server LocalDB on the development computer, with Entra External ID handling customer sign-in. A free Azure beta was deployed on October 2, 2026; see [hosted environment and deployment guide](free-hosting.md). Local development remains available.

## Selected environment

- Azure subscription: `656761a1-f733-44d2-85d9-44cb54a2f96d`, `ListHero_Subscription` (access and free deployment verified).
- Hosting subscription directory: `9c7e1e73-a0b2-48e9-a616-4747fd800348`; separate from the external customer tenant.
- Hosting region: Central US (`centralus`).
- External tenant name: List Hero.
- External tenant ID: `59a74a72-2c96-4981-9475-6b968a271e4a`.
- Primary domain: `listheroidentity.onmicrosoft.com`.
- Sign-in instance: `https://listheroidentity.ciamlogin.com/`.
- Existing Azure AD B2C tenant is separate from the new External ID tenant.
- No subscription directory transfer or changes to the existing B2C tenant have been requested.

Both local and hosted apps have the External ID instance, tenant ID, client IDs, and API scope configured. Local user secrets enable authentication and hold the web credential. The checked-in default disables authentication only for an unconfigured Development machine. Live customer sign-in and list/item creation have succeeded locally and in the hosted beta. The external tenant's subscription-linked `ciamDirectories` resource was verified.

## Completed identity configuration

Regular-browser Azure CLI administrative sign-in succeeded. The following configuration was created and read back from Microsoft Graph:

| Setting | Value |
| --- | --- |
| Web client ID | `e9f6c3be-abaf-41a7-950b-6cf638107b37` |
| API client ID | `975cdad9-848c-4431-8270-82f314890941` |
| Delegated API scope | `api://975cdad9-848c-4431-8270-82f314890941/access_as_user` |
| Customer flow | List Hero Sign Up and Sign In |
| User flow ID | `6116c465-0921-4112-acdd-8a61d4fc6b15` |
| Initial customer method | Email and password; optional display name |
| Development credential expiry | April 2, 2027, 00:31:55 UTC |

The Web registration is a confidential, single-external-tenant application with HTTPS localhost and hosted-beta sign-in/sign-out callbacks. The API issues v2 access tokens and defines `access_as_user` and an unassigned `Admin` role. The only Web delegated API permission and tenant consent are for List Hero API; no Microsoft Graph permissions were granted to List Hero. Ordinary customers need no role assignment. Administrative access to the tenant remains separate from the application's Admin role.

`scripts/Initialize-Entra.ps1` uses the project-local administrative CLI session to configure these registrations and the customer flow, retain matching registrations and local credentials, and update local settings. It stops for ambiguous registrations or unexpected permissions. It creates no Azure hosting/database resources and makes no changes to the existing B2C tenant. Its password response is captured directly and sent to .NET user secrets; secret values are not printed or written to request files. User secrets are a local development store, not a production vault.

The project owner successfully completed customer sign-in and created a test list and item. API logs confirm Entra signature, lifetime, and API audience validation; a read-only LocalDB check confirmed one active user, one active list, and one active item. On October 2, 2026 the owner reported completing the live sign-out, token renewal, and second-customer isolation checks. Integration tests also verify owner isolation with test identities. Customer sign-in is distinct from the administrative Azure CLI sign-in.

Initial verification after configuration: Release build passed with zero warnings/errors; 36 automated tests passed and the optional SQL test was skipped in that run (it passed against LocalDB during the preceding milestone work). Both HTTPS hosts started with authentication enabled. The home page returned 200 and displayed sign-in; `/account/sign-in` returned a 302 challenge to the correct External ID host/client with the configured API scope. API status returned 200; protected routes rejected missing and malformed tokens with 401. GET sign-out returned the Blazor 404 fallback; anonymous POST sign-out required authentication. The latest suite contains 154 passing cases, including all SQL/browser checks; see [current test evidence](test-coverage.md).

The tenant's public OpenID discovery endpoint was verified successfully on October 1, 2026. Its issuer is `https://59a74a72-2c96-4981-9475-6b968a271e4a.ciamlogin.com/59a74a72-2c96-4981-9475-6b968a271e4a/v2.0`. This confirms discovery is available; it does not verify administrator access or a working application sign-in.

## Information to provide

- Whether an Azure subscription already exists, and its subscription ID/name.
- Whether an **external** Entra tenant already exists, and its tenant ID and tenant subdomain (`example.ciamlogin.com`). A subscription's workforce directory and the external customer tenant can be different directories.
- Preferred Azure region for eventual hosting. If there is no preference, choose one after checking free-offer availability for the subscription.
- Existing web/API application client IDs, if already registered. Otherwise create separate List Hero Web and List Hero API registrations.

These identifiers are not passwords. Do not send passwords, MFA codes, client secrets, access tokens, or connection-string credentials in chat. Sign in to the Azure/Entra portal or Azure CLI interactively when configuration begins; store secrets directly in .NET user secrets for local development.

An authenticated administrative session is required to act on your Azure account. Azure resource permissions and Entra app-registration/user-flow permissions are separate. Use permissions appropriate to the specific operation; ownership of an Azure resource group does not automatically grant authority to configure External ID. Any tenant creation, subscription linking, admin consent, or account verification that requires human interaction must be completed in that session.

## Sign in through your regular browser

Microsoft sign-in has stalled repeatedly in the embedded browser on this computer. Use the project-local Azure CLI for setup instead. Microsoft's official Windows ZIP distribution is extracted under `.artifacts/azure-cli`; it does not require a system-wide installation. Its executable is `.artifacts/azure-cli/bin/az.cmd`.

From the repository root in PowerShell:

```powershell
.\scripts\Connect-Azure.ps1
```

The script opens Microsoft's OAuth sign-in in the normal default browser and selects the List Hero external tenant. If the browser does not open, use:

```powershell
.\scripts\Connect-Azure.ps1 -UseDeviceCode
```

For that fallback, open the Microsoft URL printed in the terminal in your regular browser and enter the terminal's device code. Passwords and verification codes stay on Microsoft's pages. The script verifies the selected tenant and creates no cloud resources.

Login/cache files are isolated under the Git-ignored `.artifacts/azure-config` directory. Every later CLI session for this project must use the same configuration directory:

```powershell
$env:AZURE_CONFIG_DIR = Join-Path (Get-Location) '.artifacts/azure-config'
& .\.artifacts\azure-cli\bin\az.cmd account show --query tenantId --output tsv
```

The external customer tenant need not contain the hosting subscription. A separate login to the subscription's owning directory may be needed later for infrastructure work; do not transfer the subscription directory to solve that distinction.

References: [official portable CLI installation](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli-windows#zip-package), [browser/device-code sign-in](https://learn.microsoft.com/en-us/cli/azure/authenticate-azure-cli-interactively#sign-in-with-a-browser).

## Development cost plan

The offers below were checked against Microsoft's documentation on October 1, 2026. Verify availability in the selected subscription and region before provisioning.

| Component | Initial choice | Free-offer considerations |
| --- | --- | --- |
| Customer identity | Entra External ID core features | First 50,000 monthly active users are free. Premium add-ons and SMS are outside this assumption. |
| Web/API compute | Local development plus hosted App Service Free (F1) beta | Free plan verified after deployment; restrictive CPU/connection quotas apply. |
| Database | LocalDB for development; separate Azure SQL free-offer beta database | Hosted free allowance and quota pausing verified; 100,000 vCore seconds, 32 GB data, and 32 GB backup monthly. |
| Images | External image URLs | No List Hero blob storage is provisioned. |
| Custom domain | Defer | Azure's default app hostname can be used for a later hosted demo. |

For an Azure SQL free database, select **Auto-pause the database until next month** when the allowance is exhausted. Avoid the option to continue with additional charges. A quota pause can make the app temporarily unavailable. Disconnect database tools when finished; open connections can prevent serverless auto-pause and consume the compute allowance.

App Service Free limits WebSockets to five connections per instance. Interactive Server keeps an active server connection for each browser tab, so this tier has little capacity for a Blazor demonstration and should not be treated as a production hosting plan. CPU and bandwidth quotas also apply. A free hosted demo may fit; local development is the recommended initial approach.

External ID requires subscription linkage for ongoing billing outside its temporary trial. Linking it does not remove the free core allowance, but overages/add-ons can still be billed. Free-tier usage is conditional on those allowances; it is not a blanket guarantee that an entire Azure account cannot incur charges.

## Identity configuration checklist

1. Create or select the external tenant and confirm its subscription linkage.
2. Register List Hero API, expose `api://{API_CLIENT_ID}/access_as_user`, and request v2 access tokens. Define the `Admin` app role as a placeholder without assigning it to ordinary users.
3. Register List Hero Web as a confidential web application. Use local redirect URI `https://localhost:7016/signin-oidc` and the appropriate signed-out callback `https://localhost:7016/signout-callback-oidc`.
4. Grant the web registration the API delegated permission and grant tenant admin consent. External tenant customers cannot supply this consent themselves.
5. Create a customer sign-up/sign-in user flow, choose the initial email sign-in method, and associate List Hero Web with it.
6. Configure each host's tenant instance/ID and its own client ID. Store the web credential locally; the API needs no client secret to validate tokens. Set web `ListHeroApi:Scopes:0` to the delegated scope and enable authentication in both hosts.
7. Run both hosts with HTTPS. Sign up a customer account, create a private list, add an item, reopen the list, and verify a second customer cannot access the owner's route. Verify sign-in/token renewal and sign-out against the real tenant.

Do not add a fake login to work around tenant setup. The test assembly uses a test-only authentication handler; the deployed API never accepts its test identity headers.

## Later Azure hosting

The [free beta guide](free-hosting.md) describes the deployed F1/SQL free-offer environment, self-contained packages, encrypted persistent keys and SQL token cache, separate migration helper, and hosted callback update. Provisioning and live checks succeeded; the owner confirmed hosted sign-in and list/item creation. No paid tier is selected as an automatic fallback when a free offer is unavailable.

## Microsoft references

- [Entra External ID pricing](https://azure.microsoft.com/en-us/pricing/details/microsoft-entra-external-id/)
- [External ID subscription linkage and billing](https://learn.microsoft.com/en-us/entra/external-id/external-identities-pricing)
- [App Service quotas](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits#app-service-limits)
- [Azure SQL free-offer FAQ](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-offer-faq?view=azuresql)
- [External tenant quickstart](https://learn.microsoft.com/en-us/entra/external-id/customers/quickstart-get-started-guide)
- [App registration and external-tenant consent](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [Blazor authentication and delegated API access](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/blazor-web-app-with-entra?view=aspnetcore-10.0)
