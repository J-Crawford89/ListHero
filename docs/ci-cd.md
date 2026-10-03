# CI and beta deployment

The workflow is [CI and beta deployment](https://github.com/J-Crawford89/ListHero/actions/workflows/ci.yml). Pull requests run tests. Pushes to `master` and manual runs on `master` also deploy the existing Azure beta, after both test jobs succeed.

## What runs

1. Ubuntu builds the .NET 10 solution and runs the ordinary tests.
2. Windows creates a fresh `ListHeroCI` LocalDB instance, verifies a readiness query, connects tests directly to its named pipe, installs headless Chromium, runs every test, and enforces the existing line/branch coverage thresholds. No skipped tests are permitted in this job. VSTest's identical coverage attachment copies are accepted; different reports are rejected. Test reports are retained for three days.
3. Ubuntu publishes self-contained Windows x86 API/web packages from the same commit and a separate migration tool. Large release packages stay on the temporary runner rather than in artifact storage.
4. The deployment job signs into Azure using GitHub OpenID Connect, verifies the existing F1 plan and SQL free/quota-pause settings, temporarily allows only the runner's IPv4 address into SQL, applies migrations, and deploys the API followed by the web host.
5. The SQL firewall rule is removed in `finally`, and live homepage, health, anonymous authorization, database-read, and sign-in challenge checks run. A subsequent deployment also removes CI rules left behind by a forcibly terminated runner.

Deployment jobs run one at a time and are not automatically canceled by a newer push. Pull requests do not run deployments or receive beta environment secrets. The GitHub `beta` environment permits only the `master` branch, and Azure trusts the subject `repo:J-Crawford89@60453593/ListHero@1402154601:environment:beta`. The permanent owner/repository IDs follow [GitHub's immutable subject format](https://docs.github.com/en/actions/reference/security/oidc).

## Identity and configuration

The deployment identity lives in the subscription's hosting tenant, **not** the External ID customer tenant. It has Reader access to `rg-listhero-beta`, Website Contributor on each of the two existing apps, and a custom role allowing SQL firewall rule management on the existing SQL server. It has no subscription-wide Contributor/Owner assignment. No Azure client secret is created.

GitHub `beta` environment variables:

| Variable | Value |
| --- | --- |
| `AZURE_CLIENT_ID` | Deployment application client ID |
| `AZURE_TENANT_ID` | `9c7e1e73-a0b2-48e9-a616-4747fd800348` |
| `AZURE_SUBSCRIPTION_ID` | `656761a1-f733-44d2-85d9-44cb54a2f96d` |

The environment secret `BETA_MIGRATION_CONNECTION` contains the existing beta database migration administrator's SQL connection. It is available only in the deployment step. It must target `tcp:sql-listhero-yj3pk6oimchdi.database.windows.net,1433` and `ListHeroBeta`. Do not put it in source, workflow text, a published artifact, or logs. Rotate the GitHub secret when that credential changes.

CI uses the migration tool's `--migrate-only` option. Initial infrastructure, token-cache table, runtime users, application settings, certificates, and key rings continue to be managed by the existing local provisioning scripts. CI preserves them, so deployment does not reset authentication or share-link keys. It does not reapply the infrastructure template or introduce paid resources.

For identity setup/recovery, sign in with `scripts/Connect-AzureHosting.ps1`, create the master-only GitHub beta environment first, then run `scripts/Configure-CiAzure.ps1`. The script reuses its registration and trust, grants scoped roles, and writes non-secret IDs to `.artifacts/ci-config/azure.json`. Set those IDs as the environment variables above. Credential transfer to GitHub requires explicit authorization; configure the migration connection through GitHub Secrets or an authorized GitHub CLI session.

## Using the pipeline

Push changes to `master` to deploy after validation. To redeploy the current commit, open the workflow's Actions page, choose **Run workflow**, and select `master`. A manual run on another branch runs tests without deployment.

If tests or migrations fail, app deployment does not start. If an app deployment or smoke check fails, inspect the failed job before rerunning. F1 does not provide deployment slots: the two updates are sequential and can cause a brief interruption. Database changes must remain compatible with the previous app version during rollout. Use additive migrations first; remove old schema in a later change.

To roll back application code, revert the affected commit and push the revert through the same tests. SQL migrations are not automatically rolled back. Do not use an old application version against an incompatible newer schema. Infrastructure recreation and database recovery remain separate maintenance operations.

The repository is public, so standard GitHub-hosted runner minutes are free under [GitHub Actions billing rules](https://docs.github.com/en/actions/concepts/billing-and-usage). Artifacts still have storage limits; reports have short retention and deployment packages are not uploaded. Azure's [existing free-tier limits](free-hosting.md) remain in effect. This workflow does not configure paid runners or change billing settings.

Reference: [Microsoft's GitHub OIDC setup](https://learn.microsoft.com/en-us/azure/developer/github/connect-from-azure-openid-connect).
