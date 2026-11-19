# Credential-expiry alerts

A separate **Credential expiry alerts** GitHub workflow checks the recorded dates daily at **14:37 UTC** and can also be run manually. It reads only `ops/credential-expiry.json`: no Azure login, Graph query, web request, database readiness check, app wake-up, credential value, or deployment is involved. It has read-only repository permission, uses the pinned checkout action, and adds no Azure service or paid monitoring resource.

The workflow is committed locally until a future release/push is authorized. No schedule or notification has been activated by this work. The existing deployment workflow still deploys on a `master` push, so do not push merely to enable alerts without authorizing that release.

## What is monitored

| Fixed inventory ID | Credential | Recorded UTC expiry date | Current source |
| --- | --- | --- | --- |
| `entra-web-secret` | Entra web sign-in client secret | 2027-04-02 | Existing hosting runbook |
| `api-key-certificate` | API Data Protection key-encryption certificate | 2029-10-02 | Public certificate in local encrypted beta backup |
| `web-key-certificate` | Web Data Protection key-encryption certificate | 2029-10-02 | Public certificate in local encrypted beta backup |

These are recorded expiry dates, **not live confirmation of the credential currently deployed**. The secret's existing runbook date is retained honestly as `runbook`; it was not revalidated against Entra. The two certificate dates were extracted locally from their public certificates inside the existing Windows-encrypted backup, without exporting keys/passwords or contacting Azure. A changed/revoked credential, changed hosted configuration, or a stale backup requires a separate verification and inventory update. `recordedOn` records the source/update date, not a claim of live verification.

An opaque client-secret value does not reveal its expiry. Microsoft Graph exposes the associated credential's `endDateTime` metadata, while returning its `secretText` only at creation. Confirm the credential actually used by the web registration when updating its date; do not substitute a different credential's later date. See [Microsoft's password credential reference](https://learn.microsoft.com/en-us/graph/api/resources/passwordcredential?view=graph-rest-1.0).

API bearer/access tokens, transient GitHub OIDC tokens, SQL password rotation policy, and user-session lifetimes are not monitored by this date inventory. The API does not have a separate confidential-client secret. Existing startup event 1002 remains an independent signal for the actual certificate loaded by each running host.

## Thresholds and output

- More than 30 days remain: `healthy` expiry status, exit 0 when all entries are healthy.
- At most 30 days remain: `due`, warning annotation and exit 1.
- At most 7 days remain: `urgent`, error annotation and exit 1.
- Expiry instant reached/passed: `expired`, error annotation and exit 1.
- Missing expiry: `unknown`, error annotation and exit 1. Unknown dates never pass as healthy.
- Missing/malformed inventory, unsupported schema, duplicate/unknown IDs, invalid dates, bad clock override, or failed output: safe error and exit 2.

Date-only records use **00:00 UTC at the beginning of the expiry date**, deliberately conservative when the provider's expiry time is later that day. Thresholds compare exact elapsed UTC time; displayed remaining days round up. Invalid input/native exception details are withheld. The allowlisted output includes only credential labels, normalized dates, provenance, and computed status. The job summary contains a table and the console also prints JSON. There is no artifact upload or issue creation.

Any alert makes the job fail so configured GitHub Actions failure notifications can be delivered. Alerts repeat on each daily run until the recorded problem is resolved; this is not automatic renewal, rollback, revocation, or a check of sign-in health.

## Activation and delivery (after an authorized push)

1. Ensure this workflow exists on the repository's default branch and GitHub Actions permits it.
2. In your GitHub notification settings, enable Actions email/web notifications, optionally failures only. A failed run is visible in Actions regardless of email preferences. For scheduled workflows, the notification recipient depends on who created/last changed the schedule or re-enabled it; verify that it is your account. See [GitHub workflow notifications](https://docs.github.com/en/actions/concepts/workflows-and-actions/notifications-for-workflow-runs).
3. Manually run **Credential expiry alerts** with **test_notification** checked. It deliberately fails with a clearly labeled notification test, without inspecting or changing credentials. Confirm delivery. Leave that option unchecked for a real date check. No notification test was sent during local implementation.
4. Confirm recent scheduled runs periodically. GitHub can delay/drop scheduled work and automatically disables public-repository schedules after 60 days of inactivity. Re-enable a disabled workflow. This is a best-effort GitHub scheduler, not an always-on SLA; if the repository will be dormant, arrange an independently approved external reminder/scheduler before relying on it for a deadline. The workflow does not manufacture commits or use write permission to keep itself alive. See [GitHub schedule limitations](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#schedule).

No GitHub account preferences, Azure permissions, Entra settings, app settings, or secrets are modified by these files.

## Local checks and updating dates

From the repository root in PowerShell 7:

```powershell
./scripts/Test-CredentialExpiry.ps1
# Optional offline look-ahead; this does not change the inventory or send anything:
./scripts/Test-CredentialExpiry.ps1 -AsOfUtc '2027-03-03T00:00:00Z'
```

The initial local check passed for the recorded dates. Review dates after every credential rotation or hosted configuration change, verify which credential is in use, and update only its non-secret record:

```powershell
./scripts/Update-CredentialExpiry.ps1 -CredentialId entra-web-secret -ExpiresOn '2030-04-02'
# Windows only: refresh both public certificate dates from the existing DPAPI backup:
./scripts/Update-CredentialExpiry.ps1 -ReadLocalCertificateBackup
```

The example secret date is a placeholder, not an actual renewal. The backup mode reads the existing backup in memory under the current Windows account and extracts public certificate expiry dates only. It supports an explicit `-CertificateBackupPath` for a recovered/synthetic backup. It neither replaces the backup nor reads Azure. It does not infer the web secret's date from the secret string. `-InventoryPath` supports an alternate reviewed/synthetic inventory for both commands. Only the selected record changes in operator mode; backup mode updates only the two certificate records. Review the diff, verify it against the intended configuration, and commit the metadata with the authorized rotation/release.

The version-1 inventory requires exactly the three fixed IDs and only `id`, nullable `expiresOn`, `source`, and `recordedOn` fields. Sources are `runbook`, `local-backup`, `provider`, `operator`, or `unknown`; `unknown` pairs with a null expiry. Do not put secret text, certificate PEM/PFX, passwords, key IDs, tenant/customer data, or connection strings in it. Other credential types require an explicit schema/policy extension and tests.

Respond to a certificate alert by planning safe rotation while preserving old keys/certificates needed for decryption. Replacing a protection certificate blindly can make retained links, cookies, guest credentials, and drafts unreadable. Use the [retention and recovery runbook](retention-and-restore.md); this feature deliberately does not automate rotation.

## Verification

Tests invoke the actual PowerShell checker/updater in child processes with cloud CLIs absent from `PATH`. They cover exact 30-day/7-day/expiry boundaries, every credential independently, unknown dates, malformed/untrusted inventory, safe summaries/failures, operator updates, and extraction from a synthetic Windows-protected backup. On non-Windows hosts the unsupported backup mode fails safely. No real backup is read by automated tests. Workflow checks enforce the separate schedule/manual notification test, pinned read-only checkout, and absence of secrets, cloud login, deployment, and application polling. Actionlint validates the YAML. Existing application SQL/browser/coverage checks remain unchanged.

All 508 tests passed with no skips, including SQL Server and headless Chromium; all nine unchanged application coverage gates passed. Evidence: `.artifacts/qol-credential-expiry-final/coverage.trx` and its coverage attachment. The actual local checker returned healthy for the recorded dates. No notification delivery, hosted certificate match, or live Entra credential verification is claimed from these local tests.
