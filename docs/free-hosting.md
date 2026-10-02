# List Hero free Azure beta

The hosted beta uses the existing subscription `656761a1-f733-44d2-85d9-44cb54a2f96d` in **Central US**. Its customer identity tenant remains separate: `59a74a72-2c96-4981-9475-6b968a271e4a`. Do not transfer the subscription into the customer tenant.

## Deployed environment

Deployment completed October 2, 2026:

- Web: <https://listhero-yj3pk6oimchdi.azurewebsites.net/>
- API: <https://listhero-api-yj3pk6oimchdi.azurewebsites.net/>
- Resource group: `rg-listhero-beta`.
- Hosting plan: `asp-listhero-beta`, verified **F1 / Free**.
- SQL server: `sql-listhero-yj3pk6oimchdi`; database: `ListHeroBeta`.
- SQL free allowance and `AutoPause` exhaustion behavior verified after creation.
- Existing External ID subscription linkage verified; hosted callbacks added while retaining localhost callbacks.
- Live homepage, API health/status, anonymous owner-route rejection, a real SQL-backed missing-list read, and the correct sign-in challenge all passed.

This database starts separately from LocalDB; local test lists were not copied. The owner confirmed hosted customer sign-in and test list/item creation on October 2, 2026. A two-browser sharing journey and hosted sign-out/renewal remain beta acceptance checks.

## What is free, and what happens at the limit

| Component | Choice | Practical limit |
| --- | --- | --- |
| Web app and API | Two Windows App Service apps on an F1 plan | 60 CPU minutes per app per day; five WebSocket connections per instance. Interactive Server normally uses a connection for each open tab. Idle apps sleep; first requests can be slow. |
| SQL database | Azure SQL General Purpose serverless free offer | 100,000 vCore seconds, 32 GB data, and 32 GB backup monthly. The database pauses until the next month if its free allowance runs out. |
| Customer identity | Existing Entra External ID Basic features | First 50,000 monthly active users are free. Paid identity features and SMS are outside this setup. |
| App addresses and HTTPS | Azure-provided `azurewebsites.net` addresses | A custom domain is deferred. |
| Item images | External URLs | No paid upload/storage service is provisioned. |

This is a small test environment. A page left open does not consume a CPU minute every minute, but Blazor interactions, rendering, and background refresh use CPU. Multiple tabs can hit the connection limit quickly. The SQL allowance measures compute activity: its duration depends on the active vCores and memory usage. Avoid leaving database administration tools connected.

The deployment explicitly sets `useFreeLimit=true` and `freeLimitExhaustionBehavior=AutoPause`. It does **not** choose the option to continue for additional charges. That paid-overage option cannot be switched back to quota pausing. An exhausted allowance means temporary unavailability.

The template creates no paid hosting tier, Blob Storage, Redis, Key Vault, Application Insights, Log Analytics, custom domain, or paid backups. Subscription eligibility and actual F1 quota were verified for this deployment. Verify again before another environment is provisioned. No paid fallback is authorized. This deployment does not control charges from unrelated resources already in the subscription.

Microsoft references checked October 2, 2026: [App Service quotas](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/azure-subscription-service-limits#azure-app-service-limits), [SQL free offer](https://learn.microsoft.com/en-us/azure/azure-sql/database/free-offer?view=azuresql), [External ID pricing](https://www.microsoft.com/en-us/security/pricing/microsoft-entra-external-id/). [Budget alerts](https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-create-budgets) notify; they do not impose a spending cap.

## Steps

For routine deployments from GitHub, see [CI and beta deployment](ci-cd.md). The steps below provision/reconfigure the environment locally; CI updates the existing apps and applies migrations without replacing their configuration or keys.

Run these from the repository root in PowerShell. Microsoft sign-in uses the regular browser because embedded-browser sign-in has stalled on this computer. Passwords and MFA codes stay on Microsoft's pages.

1. Run `./scripts/Connect-AzureHosting.ps1` and sign in with the subscription owner's account. Use `-UseDeviceCode` only if the normal browser sign-in fails.
2. Run `./scripts/Get-AzureHostingPreflight.ps1`. This is read-only: it verifies the account, reviewed free template, F1 region listing, and provider registrations. Deployment validation subsequently checks actual quota and SQL eligibility.
3. Run `./scripts/Publish-FreeBeta.ps1 -PrepareOnly` to build self-contained Windows packages and the separate database migration tool. This creates no Azure resources.
4. Deploy the prepared release with `./scripts/Deploy-FreeBeta.ps1 -ReleaseDirectory <prepared-directory>`, or run `./scripts/Publish-FreeBeta.ps1` to prepare and deploy together. The script validates the template, provisions the free resources, restricts SQL firewall access to the apps and a temporary migration address, applies migrations, and publishes both apps. It stops on an error; it does not select a paid alternative.
5. Run `./scripts/Update-BetaIdentity.ps1` to add the hosted sign-in/sign-out callbacks while keeping local callbacks. If customer-tenant authorization has expired, run `./scripts/Connect-Azure.ps1`, then retry this step. The hosting subscription and customer directory require separate authorization.
6. Confirm the external tenant's subscription linkage in the Entra portal if it is not already linked. Human account verification may be required; no subscription directory transfer is needed. Keep the initial sign-in methods within the Basic/email feature set.
7. Run `./scripts/Test-FreeBeta.ps1` for repeatable live HTTP/database-read/authorization/challenge checks. Open the hosted URL in a regular browser and manually test sign-in, private list/item creation, a shared link in another browser, purchase marking, owner privacy, and sign-out. Cloud acceptance is separate from the local tests.

## Secrets and restarts

No credentials belong in Git or chat. Deployment reads the configured web credential from local .NET user secrets. That existing credential expires April 2, 2027; renew it before that date and update the encrypted deployment configuration and app setting together.

The database migration administrator is separate from the runtime users. The API receives access to its application tables; the web host receives access only to its token-cache table. Migrations run as a deployment step, never automatically at application startup. The deployment helper refuses a database name other than `ListHeroBeta`.

Both apps persist Data Protection keys under their own App Service `HOME/data/<application>/keys` directory and encrypt them with separate certificates. Certificate settings contain base64-encoded public certificates and password-encrypted PEM private keys. Loading attaches the RSA key in memory and avoids the native Windows PFX import that failed on the free host. The deployment can convert the initial encrypted PFX backup to PEM without changing its certificate/key identity. The web token cache lives encrypted in SQL. Database pooling is disabled for this small beta to allow SQL to pause when it is idle; API operations retry transient database failures and resumes. The first database request after a pause may still take time.

Generated credentials and certificate backups are protected with Windows DPAPI for the current Windows account in Git-ignored `.artifacts/azure-hosting/beta-secrets.dpapi`. The script reuses them on redeployment and refuses to generate replacements over existing resources if the backup is missing. Preserve this file securely together with the Windows account's ability to decrypt it. It is not a portable backup. The hosted key-ring files must also be preserved before deleting/replacing either app: restoring only a certificate cannot recreate lost encryption keys.

Live Azure validation, provisioning, migration, publication, HTTP checks, and owner-reported customer sign-in/creation completed successfully. Additional hosted collaboration checks are separate from the automated local suite.
