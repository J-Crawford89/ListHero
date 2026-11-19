# Operational diagnostics

Implemented locally November 18, 2026. No push, live Azure operation, deployment, database migration, telemetry subscription, or paid monitoring resource is included. Existing authentication, authorization, token encryption, free-tier hosting safeguards, and database retry policies remain in place.

## Finding and correlating events

The API and web host write structured JSON to the existing console output. Local host scripts write it to `.artifacts/local-Api.stdout.log` and `.artifacts/local-Web.stdout.log`. On a future Azure deployment the same application output is available through the host's configured application logging; this change does not enable a new Azure logging service or change retention/billing settings.

Startup records include a fresh process-instance ID and the assembly's build-time informational version (including the source revision when built from Git). Rebuild after committing when checking the local release ID. An instance change establishes a new process, not the reason for the restart. Certificate startup records contain only the key-protection certificate expiry date and days remaining, with a warning at 30 days. They contain no certificate subject, thumbprint, password, or key. This remains a startup signal; the separate [credential-expiry alerts](credential-expiry-alerts.md) workflow checks recorded dates daily without waking the app. It cannot derive an expiry from an opaque Entra secret or verify hosted configuration automatically.

Each host HTTP request receives a server-generated `X-ListHero-Request-Id`; caller-provided values are ignored. API problem details also contain that ID as `requestId`. HTTP log scopes include this request ID, host, instance, and release. Route names are templates, not actual list IDs, handles, URLs, or query strings. The HTTP client adapter logs the API's validated request ID, allowing a failed web-to-API call to be matched with the API record. Token/circuit operations also have fresh operation IDs; they are not user/session identifiers.

Normal requests, successful token/cache operations, and cache hits/misses use Debug. Requests/calls taking at least one second use Information; server failures use Error/Warning. Startup and session transitions use Information. This avoids turning viewer polling and normal browsing into an Information-level request ledger. To investigate locally, temporarily set these application categories to Debug:

```json
{
  "Logging": {
    "LogLevel": {
      "ListHero.Diagnostics": "Debug",
      "ListHero.Client.Api.ApiDiagnosticsHandler": "Debug"
    }
  }
}
```

Keep framework HTTP-client URL logging, EF payload/exception logging, identity PII logging, and raw authentication diagnostics disabled. Targeted filters replace those noisy categories with the application events below. The redaction contract applies to these application events and filters; third-party Azure action output or separately enabled platform access logs have their own behavior. New diagnostics never pass exception objects, raw exception messages, bodies, cookies, authorization/share/guest headers, cache keys/values, user IDs, or private return destinations to the logger.

## Event reference

| IDs | Purpose |
| --- | --- |
| 1000 / 1002 | Process startup / key-protection certificate expiry |
| 1001 | HTTP completion: route template, status, elapsed milliseconds, failure category |
| 2000–2003 | Sign-in challenge, successful identity validation, validation failure, remote provider failure |
| 2004–2006 | API token acquired, sign-in recovery required, token acquisition failure |
| 2100 / 2101 / 2103 | App session created, absolute-age rejection, protected-return/antiforgery/sign-out outcomes |
| 2200 | Distributed token-cache read/write/refresh/remove, hit/miss/success/failure/cancellation |
| 2300 | Circuit activity failure, preserving the original exception behavior |
| 3000 | Database readiness result and elapsed time |
| 3100 / 3101 | Outgoing API result/reference ID or transport-failure category |

Failure categories are a closed set such as database, network, timeout, canceled, or unexpected. A database category is not proof of quota exhaustion, bad credentials, or a cold start. Use the timing, correlated request, readiness result, and provider status together. Distributed-cache hit/miss describes L2 storage only; it does not establish whether MSAL served a token from L1 memory or contacted Entra. Cache and token failures are rethrown; encryption, fallback, cancellation, and cache ownership/disposal are preserved.

## Liveness, readiness, and free-tier cost

- `GET /health/live` is process liveness and performs no SQL check. `/api/status` retains its existing process-availability contract.
- `GET /health/ready` checks database connectivity with a five-second cancellation budget. It returns `ready` (200) or `unavailable` (503), with no server/database names or exception details and `Cache-Control: no-store`. Driver cancellation may take time to complete. Readiness does not prove schema version, write permissions, token-cache permissions, or every application workflow.
- Readiness can wake an auto-paused SQL database and consume its allowance. Do not configure it as a frequent uptime poll on the free beta. No recurring readiness poll or App Service health-path setting was added. The deployment verification script calls it once after its existing database-read check.

## Deployment failures

The CI deployment script records a phase such as preflight, runner-firewall, migration, api-deploy, web-deploy, firewall-cleanup, or verification. It captures native migration output instead of forwarding potentially sensitive SQL/credential errors. Raw command and exception details are withheld from the final error. The tested manifest must identify a commit; GitHub summaries/JSON accept only a valid commit hash and numeric run ID, otherwise using unknown/local.

The allowlisted record is written to `.artifacts/deployment-diagnostic.json` and the GitHub step summary. Packaging/authentication failures before deployment have their own summary step. A constrained `LISTHERO_DIAGNOSTIC_DIRECTORY` can place synthetic records in an isolated directory under repository artifacts. No new cloud resource or artifact-upload service is involved. A forcibly terminated runner may not execute its catch/finally blocks, and this change does not automatically retry deployments or roll back migrations/apps.

## Verification

All 349 tests passed without skips, including SQL Server and headless-browser journeys; all nine assembly coverage gates passed. Evidence is retained locally in `.artifacts/qol-diagnostics-complete/coverage.trx` and its coverage attachment.

Canary tests check that headers, URLs/queries, list titles, return destinations, identity data, cache keys/values, and exception messages do not enter captured diagnostics. They verify request/problem reference agreement, database/network/timeout/cancellation classification, cache transparency/disposal, circuit rethrow behavior, healthy/unavailable SQL readiness, and liveness independence. Deployment tests exercise the actual preflight failure with Azure CLI absent from the child process path, isolated phase summaries, and a fake migration process that emits a synthetic credential to stderr and exits unsuccessfully. Its native output is withheld. These tests cannot call Azure. Live hosted logging, cold-start timings, and deployment-stage failures remain acceptance checks for a separately authorized release.
