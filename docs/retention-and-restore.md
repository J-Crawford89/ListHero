# Retention and recovery

Implemented locally November 18, 2026. These are operator commands, not HTTP endpoints or scheduled jobs. This release does not run cleanup automatically, change Azure resources/settings, add a migration, or restore the live beta.

## Retention policy

| Data | Current policy |
| --- | --- |
| Users, lists, items, share links, purchase marks | Retain, including archived, revoked, expired, and undone records. No automatic business-history purge. |
| Guest identities | Retain, including currently unreferenced identities. The model has no guest expiry/last-used timestamp. Age alone cannot establish that an identity is unused; existing browser credentials, own-mark undo, and idempotent retries must keep working. |
| Archived saved-list shortcuts | Retain. They are business history and can participate in re-saving/concurrency behavior. |
| SQL token cache | Preview expired rows; explicit apply removes at most 1,000 per invocation, only after expiry plus a 24-hour grace period. Recently expired and live rows remain. Missing cache table is a successful no-op. |
| Local isolated SQL test databases | At least 14 days old, exact generated name, explicit test-ownership marker, and no connected sessions. Apply requires one exact target. Never force disconnects. |
| Data Protection keys and revocation records | Retain all, together with certificates/private keys needed to decrypt them. Expired keys can still be required to decrypt old payloads. Never purge keys based on age. |

An archived-owner access policy and any future permanent erasure feature require a separate product/security decision. These commands do not introduce permanent user-data deletion.

## Build and invocation

From the repository root:

```powershell
dotnet build scripts/Maintenance -c Release
dotnet scripts/Maintenance/bin/Release/net10.0/Maintenance.dll --help
```

Examples below use that built DLL. Commands emit a JSON report and exit 0 on success or 1 with a safe error category. Raw SQL, paths, credentials, exception messages, and backup contents are not printed in failures. Do not put credentials in command arguments, source control, shell history, or chat. Supply connection strings and passphrases through process environment variables populated by a trusted local secret source; clear them in `finally` blocks. Environment values are accessible to sufficiently privileged local processes.

## Review cache cleanup

Set `LISTHERO_MAINTENANCE_CONNECTION` to the database containing the application tables and optional `cache.TokenCache` table. Use a narrowly scoped maintenance identity with read access to the counts and delete access to the cache. Runtime application identities are deliberately more restricted. The database name must exactly match the connection's initial catalog.

```powershell
dotnet scripts/Maintenance/bin/Release/net10.0/Maintenance.dll retention --database ListHero
# Review the report, then opt in to a bounded batch:
dotnet scripts/Maintenance/bin/Release/net10.0/Maintenance.dll retention --database ListHero --apply --batch 100
```

The report counts retained archived lists/items/shortcuts, marks, and guests, and reports eligible/deleted cache rows separately. Inspection and deletion are not one transaction: concurrent cache activity may change counts. Deletion rechecks expiry at execution and never touches business tables. Repeating apply is safe. This command supports SQL Server/Azure SQL connections, but no live beta invocation is included in this work.

## Review local test databases

Set `LISTHERO_MAINTENANCE_CONNECTION` to `(localdb)\MSSQLLocalDB` or `(localdb)\ListHeroCI` with Windows integrated authentication. The CI LocalDB named-pipe form is also supported. Remote servers and SQL authentication are refused before connecting. Inventory is bounded to 500 online, writable, nonsnapshot candidates per invocation; use an exact target if needed.

```powershell
dotnet scripts/Maintenance/bin/Release/net10.0/Maintenance.dll test-databases
dotnet scripts/Maintenance/bin/Release/net10.0/Maintenance.dll test-databases --database ListHero_Integration_REPLACE_WITH_32_LOWERCASE_HEX_DIGITS_Core
# Apply only a reviewed exact name from the report:
dotnet scripts/Maintenance/bin/Release/net10.0/Maintenance.dll test-databases --database ListHero_Integration_REPLACE_WITH_32_LOWERCASE_HEX_DIGITS_Core --apply
```

The placeholder is intentionally invalid: substitute a real generated name. Accepted names are `ListHero_Integration_<32 lowercase hex digits>` with an optional alphanumeric/underscore suffix up to 64 characters. Future databases created through the test host/fixture are marked immediately after a successful `CREATE DATABASE` with database extended property `ListHero.TestDatabase=ListHero.Tests.v1`. Existing databases are never adopted. Legacy/unmarked databases remain for manual review, even when old. The marker is an ownership guard, not an authentication boundary against a local SQL administrator.

Apply rechecks ownership, creation time, and current sessions. A sleeping pooled connection counts as in use. Close the test host and allow connections to drain; the tool does not clear other programs' pools, switch to `SINGLE_USER`, or roll back sessions. New connections that race with deletion cause SQL Server to refuse the drop. Files outside the database are not deleted.

## Portable key-ring bundles

Back up API and web rings separately with their exact application names, `ListHero.Api` and `ListHero.Web`. Include every required key XML and revocation XML file. The tool validates that the supplied certificate can decrypt every key before exporting. It supports plaintext development rings and certificate-encrypted rings; machine-bound DPAPI-encrypted rings are refused. It currently accepts one RSA certificate per bundle: if keys require several rotation certificates, preserve them all separately and extend/test the tooling before relying on this format.

Set `LISTHERO_BACKUP_PASSPHRASE` to a strong, unique secret of 16–1,024 characters. For encrypted rings also supply:

- `LISTHERO_KEY_CERTIFICATE_BASE64`: base64 UTF-8 PEM containing the public certificate and password-encrypted RSA private key, matching the hosted certificate format.
- `LISTHERO_KEY_CERTIFICATE_PASSWORD`: password for that PEM.

```powershell
dotnet scripts/Maintenance/bin/Release/net10.0/Maintenance.dll backup-keys --application ListHero.Api --source C:\secure\api-keys --output C:\secure\new-api-backup
dotnet scripts/Maintenance/bin/Release/net10.0/Maintenance.dll restore-keys --application ListHero.Api --source C:\secure\new-api-backup\keys.lhb --output C:\secure\new-api-restore
```

Export/restore require a **new** output directory. On Windows it is restricted to the current account; on Unix it is mode 700 and files are mode 600. No existing target is overwritten, merged, or removed. Wrong password, altered ciphertext, application mismatch, unsafe filenames, and duplicate entries are rejected. A later filesystem or key-validation failure can leave a partial new output directory; inspect and choose a different new directory on retry.

`keys.lhb` is a versioned application bundle, not a password-protected ZIP compatible with desktop archivers. Format `LHKEYS01` contains a 16-byte random salt, 12-byte nonce, 16-byte authentication tag, and AES-256-GCM encrypted ZIP payload. The 36-byte header is authenticated; the key is derived with PBKDF2-SHA-256 and 600,000 iterations. Payload is limited to 16 MiB, individual entries to 1 MiB, and restore entries to 4,096. The encrypted manifest identifies version/application/certificate presence. The bundled PFX exists in plaintext only in memory inside the authenticated encryption boundary. Keep the passphrase separately from the bundle; losing either prevents recovery.

Restored `keyring/` contains original key IDs, material, and revocations. When included, `certificate.pem` contains the original certificate and password-encrypted PKCS#8 private key, encrypted using the **backup passphrase**. Install this material through the existing host's secure configuration mechanism; never log it. Preserve application names and protection purposes. Successful decryption does not reverse revocation, expiry of protected payloads, tenant policy, sign-out, or account removal. A certificate bundle alone cannot recreate lost Data Protection keys. Microsoft documents the consequences of [key deletion](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings) and [revoked-key recovery](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/consumer-apis/dangerous-unprotect?view=aspnetcore-10.0).

The existing `.artifacts/azure-hosting/beta-secrets.dpapi` is still a machine/account-bound deployment backup, not a portable key-ring backup. Keep it and its Windows recovery dependencies. These commands do not fetch hosted files, change app settings, or export the existing beta's certificates.

## Repeatable local restore drill

Use a permitted LocalDB connection with Windows authentication. Its initial catalog is ignored: the drill creates two unpredictable `ListHero_RestoreDrill_<nonce>_*` databases. The supplied application's database is never opened.

```powershell
dotnet scripts/Maintenance/bin/Release/net10.0/Maintenance.dll restore-drill --output C:\secure\new-synthetic-drill
```

The drill:

1. Creates/migrates a fresh source database and seeds synthetic private lists, active/archived items, a share link, guest identity, purchase, and archived saved shortcut.
2. Takes native `BACKUP ... COPY_ONLY, CHECKSUM`, runs `RESTORE VERIFYONLY`, restores into a separate database with separate files, and runs `DBCC CHECKDB`.
3. Compares every seeded entity table including row versions/history, restores separate API/web key bundles, recovers the stored share token, verifies the original guest credential against its restored hash, and decrypts a protected cookie ticket and a synthetic cache probe.
4. Removes only databases matching this run's random names **and** ownership nonce, whose files are under this run's output directory. Never force-disconnects users. The JSON report must show all eight checks true, including temporary-database removal.

The cache probe verifies encryption-key recovery, not an actual Entra/MSAL token refresh. Guest credential recovery requires the original browser credential: a stored hash cannot reconstruct a lost credential. Cookie decryption alone does not establish that Entra/API sessions remain valid. No real customer credentials or existing business records are used.

Output contains only synthetic data, including `synthetic.bak`, `drill.json`, source/restored key directories, and encrypted test bundles. The random synthetic passphrase is not retained; the test bundles themselves are disposable. The drill does not recursively delete artifacts. On failure, inspect `drill.json` and ownership/file locations before manually removing leftover synthetic databases; never run a broad prefix-based drop. Store real backup copies outside the repository/workstation failure domain through a separately reviewed process.

## Live recovery checklist (manual, not executed)

1. Establish a consistent restore point. Quiesce writes and key rotation while capturing the database and both complete key rings/certificates, or take the SQL restore point first and then capture rings containing all keys used by that point. Retain older keys/certificates; record application settings, schema version, and release commit without copying secrets into reports.
2. For Azure SQL use its supported backup/PITR workflow. A point-in-time restore [creates a new database](https://learn.microsoft.com/en-us/azure/azure-sql/database/recovery-using-backups?view=azuresql); restoring/provisioning it can incur cost. Obtain separate authorization for the target/resources before execution. A LocalDB `.bak` drill is not a live Azure recovery test or an Azure export/import implementation.
3. Restore into an isolated environment. Match the application version to the restored schema, restore original key rings/certificates and configuration, and keep customer traffic/writes off. Do not rotate/recreate certificates as a substitute for recovery.
4. Check integrity, record counts/history, owner privacy, archived exclusion, shared-link access/revocation/expiry, original guest own-mark undo/idempotency, saved-list access, sign-in, and a real API token refresh. Losing a token-cache entry may require sign-in; never relax authentication to make a restore pass.
5. Review results and obtain separate approval for cutover. Preserve the original database/keys for rollback. Reconcile writes after the restore point; backups cannot recover transactions they never captured.

No production recovery-time or recovery-point guarantee is claimed until this live procedure has been exercised and its results measured.

## Local verification

All 409 tests passed with no skips, all nine unchanged coverage gates passed, and Release build completed with zero warnings/errors. Evidence is in `.artifacts/qol-retention-complete/coverage.trx` and its coverage attachment. The built console's read-only local inventory inspected 258 candidates, with 34 owned, none eligible, and zero removed. No cleanup of existing business data or live Azure operation was performed. Synthetic test databases used by the drill and explicit drop scenarios were removed by their scoped test paths; other test artifacts remain available for inspection.
