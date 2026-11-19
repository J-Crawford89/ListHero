# Portable beta backup capture

This operator tool captures the existing beta into one encrypted, portable file. It is separate from application deployment and runs only when explicitly invoked. Implementation and synthetic recovery tests are local; **no live beta capture or hosted restore has been performed**.

## Contents and boundaries

`capture.lhb` contains:

- A DacFx BACPAC with the database schema and all table data, including archived records, guest credential hashes, purchase history, EF migration history, and SQL token cache. No table filter is applied.
- Each app's deployed `site/wwwroot` ZIP, including configuration files and binaries. Both entry assemblies must identify the same 40-character source commit in their informational version. The manifest records that deployed commit, rather than assuming the operator's local checkout matches production.
- Each app's ARM site metadata, app settings, web configuration, and connection-string configuration. These contain credentials and are encrypted with the rest of the archive.
- Separate validated `ListHero.Api` and `ListHero.Web` key-ring bundles, including all key/revocation XML and the original certificate/private key needed to decrypt the ring. The existing [portable key-ring format](retention-and-restore.md#portable-key-ring-bundles) is used inside the outer archive.
- An encrypted manifest with capture start/end UTC, fixed subscription/group/database identity, consistency acknowledgement, deployed commit, and SHA-256/size inventory for all 11 payload files.

This is application recovery material, not a complete Azure/Entra/GitHub account backup. It does not export Entra consumer accounts, tenant policies, app registrations/consents, GitHub secrets, Azure role assignments, SQL server logins, DNS, or future blob/file stores. Keep infrastructure templates, source history, tenant/subscription access, and independent account recovery methods. SQL contained-user passwords/server logins may need to be recreated when importing; never expose the recovered settings to do that. The original operator SQL password and ARM token are not added to the archive.

Key Vault references, custom key-ring paths, machine-bound keys, missing keys/certificates, and rings requiring several certificates are refused. Resolve and test those dependencies explicitly before extending this implementation. A hash cannot reconstruct a lost guest credential; original browser credentials are still required for their existing purchase marks. An expired/revoked Entra session or client secret is not made valid by restoring its bytes. A BACPAC import can regenerate SQL rowversions: old open forms can receive safe concurrency conflicts and should be reloaded after recovery.

## Consistent capture and access

A BACPAC is a logical export, not an Azure SQL native/PITR backup. Microsoft requires [no write activity during export, or a transactionally consistent database copy](https://learn.microsoft.com/en-us/sql/tools/sqlpackage/sqlpackage-export). This tool uses a maintenance window to avoid provisioning another database or a storage account.

Before a live invocation:

1. Arrange downtime; pause deployments and every other SQL writer/maintenance task. Stop **both existing beta apps** through Azure. Merely taking a browser offline is insufficient. Do not rotate keys/settings/certificates during capture. Allow in-flight work to drain.
2. Sign in to the hosting subscription with `scripts/Connect-AzureHosting.ps1`. The operator needs ARM read access to the fixed sites, permission to read their app settings/connection strings, and [Kudu access](https://learn.microsoft.com/en-us/azure/app-service/resources-kudu#rbac-permissions-required-to-access-kudu). The existing deployment identity is not automatically granted additional access by this tool.
3. Ensure the workstation can reach the SQL server under the existing firewall policy, and supply a SQL identity with sufficient DacFx export/schema/data permissions. The beta's limited runtime identities are not general backup identities. No firewall rule, role, publishing credential, or Azure resource is created/changed automatically. Do not enable basic publishing authentication; the tool uses an ARM bearer token for ARM/Kudu, with redirects disabled and fixed HTTPS destinations.
4. Prepare an encrypted workstation disk with sufficient free space. Set `LISTHERO_MAINTENANCE_CONNECTION` from a trusted secret source for the exact beta SQL endpoint/catalog with `Encrypt=True;TrustServerCertificate=False`. Set `LISTHERO_BACKUP_PASSPHRASE` to a strong unique 16–1,024 character secret, retained separately in your password manager. Never place either value in command arguments, shell history, a transcript, or chat.

The command checks the apps are `Stopped` before copying and after export, then rechecks settings, connection/web configuration, complete key-ring contents, and every deployed file's hash. Observed changes fail capture. These checks **do not replace the operator's exclusion of other writers/deployments** or detect every possible transient change. If Kudu is inaccessible while stopped, leave the capture failed and investigate permissions/platform behavior; do not restart writers and claim the resulting export is consistent. Live Kudu authorization/access has not been acceptance-tested in this batch.

## Capture

Build first, before the maintenance window:

```powershell
dotnet build scripts/LiveBackup/ListHero.LiveBackup.csproj -c Release
# After the prerequisites above; secret environment values already populated:
./scripts/Capture-BetaBackup.ps1 -OutputDirectory C:\secure\new-listhero-capture -WritersQuiesced
```

The wrapper uses the existing project-local Azure CLI login (or `-UseSystemAzureCli`), obtains a short-lived management token in memory, invokes the built tool, and restores its token/config environment overrides. It refuses existing output paths and repository paths outside ignored `.artifacts`. The console can also be invoked directly:

```powershell
# Additionally supply LISTHERO_BACKUP_ARM_TOKEN from a trusted secret source.
dotnet scripts/LiveBackup/bin/Release/net10.0/ListHero.LiveBackup.dll capture-beta --output C:\secure\new-listhero-capture --writers-quiesced
```

Clear the secret process environment values you supplied in a `finally` block. Environment values are accessible to sufficiently privileged local processes. Native errors are withheld; reports contain only a safe outcome, verification flag, deployed commit, and payload count (or an allowlisted failure category).

The destination is a **new** directory restricted to the current Windows account, or Unix mode 700. Plaintext is staged beneath its private `work` directory; DacFx table buffering is explicitly directed there and schema storage uses memory. DacFx itself is a third-party library and filesystem/encrypted-disk hygiene remains an operator responsibility. Successful capture verifies authenticated decryption and every payload's hash, removes staging, then renames `capture.partial` to `capture.lhb`. Only that final filename denotes success. Database export/package loading and hashes are not a substitute for an actual restore test.

On an ordinary caught failure, staging cleanup is attempted. A process kill, power failure, locked file, or filesystem failure can leave **sensitive plaintext staging** or a partial encrypted file. Outputs are never overwritten, merged, or broadly deleted; inspect the private failed directory and choose a new path on retry. Cleanup refuses symlinks/reparse points. It does not promise secure erasure of SSD/backup/swap contents.

After success, confirm the report, move a copy of the final encrypted file to storage outside this workstation/repository failure domain, and keep the passphrase separately. Resume the previously running apps/writers explicitly and check their health; the tool never stops/restarts them on your behalf, including on failure. Running an export can resume Azure SQL and consume its free compute allowance, and normal data-transfer/storage charges may apply; no paid service, database copy, or storage account is provisioned by this tooling.

## Offline verification and unpacking

Use a fresh private path on the recovery machine. Only the passphrase is required: no original Windows account, DPAPI deployment backup, Azure CLI login, ARM token, or SQL connection is needed.

```powershell
dotnet scripts/LiveBackup/bin/Release/net10.0/ListHero.LiveBackup.dll unpack --source C:\secure\capture.lhb --output C:\secure\new-listhero-recovery
```

This authenticates the **entire** outer archive before decrypting, validates its version, identities, exact file inventory and hashes, and extracts only allowlisted paths. It restores the nested rings to `api/protection/keyring` and `web/protection/keyring`, with original certificates in `protection/certificate.pem`, password-encrypted using the backup passphrase. Original app settings remain private JSON; recovered source certificate passwords are there as well. No SQL import, Azure write, deployment, app start, or cutover occurs. Later validation failure can leave private partial recovered files; never reuse an existing output directory.

Unpacked output contains readable customer data and credentials. Keep staging/recovery directories outside the repository or inside ignored `.artifacts`, and never upload them as CI artifacts. The direct console accepts operator-chosen paths; the wrapper's repository-path guard does not apply to direct invocations.

The outer `LHBACK01` format is a versioned application envelope, not a desktop-archiver ZIP. A 40-byte header contains magic/version, random 16-byte salt, and 16-byte IV. PBKDF2-SHA-256 (600,000 iterations) derives independent 256-bit encryption and MAC keys. Streaming AES-256-CBC/PKCS7 ciphertext is followed by HMAC-SHA-256 over the header and complete ciphertext; MAC verification precedes any decryption. The sealed file limit is 64 GiB. Site downloads are limited to 512 MiB each (expanded site fingerprints to 1 GiB), key ZIPs to 16 MiB, individual key XML to 1 MiB, and ARM responses to 1–4 MiB. Limits fail closed rather than silently omit data.

Use the [live recovery checklist](retention-and-restore.md#live-recovery-checklist-manual-not-executed) to import into a separately authorized isolated target, configure identities/permissions securely, and validate actual workflows before any cutover. Azure SQL PITR remains a separate recovery option. Neither hosted recovery time nor recovery point is guaranteed until a live capture and isolated hosted rehearsal have been measured.

## Verification

Automated tests use synthetic settings/certificates and an HTTP fake for fixed ARM/Kudu reads, plus a real isolated LocalDB DacFx export, sealed capture, offline unpack, BACPAC import into a new database, and integrity/data/key-protection checks. They cover running/wrong-resource preflight rejection; missing release and malformed/key traversal/duplicate/oversized key rejection; changed settings/configuration/key/site detection; encrypted archive password/tamper/truncation rejection; manifest/hash/path validation; safe error output; and staging removal. No real beta credentials or records are used.

All 530 cases passed with no skips, including the complete SQL/browser suite and six actual PowerShell wrapper cases using synthetic CLI responses. All ten coverage gates passed: the nine unchanged application gates plus the tool's 90% line / 80% branch requirement (measured 99.3% / 88.7%). Evidence: `.artifacts/qol-live-backup-complete/coverage.trx` and its coverage attachment. Live ARM/Kudu access, beta capture, off-machine storage, and hosted import/cutover are not verified by these local tests.
