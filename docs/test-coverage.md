# Test coverage

Updated 2026-10-02 after the coverage milestone. The suite contains 150 cases: 144 ordinary tests, five opt-in SQL tests, and one opt-in browser regression that also uses SQL. The full run includes every case; the default run skips six environment-dependent cases.

## Measured result

The full Release suite passes with no skipped tests. The Release build has zero warnings and errors. Coverage is collected across all nine application assemblies with `coverage.runsettings`, excluding generated OpenAPI code, build output, and EF migrations/model metadata. Migration execution is still verified by SQL tests. Razor component code remains included.

| Assembly | Executable lines | Branches |
| --- | ---: | ---: |
| ListHero.Domain | 100.0% | 90.4% |
| ListHero.Application | 98.7% | 91.8% |
| ListHero.Infrastructure | 96.6% | 68.2% |
| ListHero.Api | 99.5% | 82.3% |
| ListHero.Contracts | 95.7% | 100.0% |
| ListHero.Client | 97.4% | 100.0% |
| ListHero.Client.Api | 100.0% | 98.1% |
| ListHero.UI | 95.4% | 86.1% |
| ListHero.Web | 89.9% | 74.0% |

Aggregate: 1,228/1,265 executable lines (97.1%) and 641/734 branches (87.3%). Line coverage measures execution; branch coverage measures decision alternatives. Assertions and realistic workflows provide the behavioral evidence. Neither percentage promises every possible failure has been tested.

Final local evidence: `.artifacts/coverage-milestone/coverage.trx` and `.artifacts/coverage-milestone/0ceb5ff5-c233-4583-9e00-d1c4e3446b06/coverage.cobertura.xml`. Both are Git-ignored.

The original audit had 50 passing cases, low client HTTP coverage, no Web host measurement, and no continuous SQL/coverage enforcement. Its unfiltered totals included generated code, so its aggregate percentage should not be compared directly with this consistently filtered baseline.

## Gaps closed

- **Permissions and privacy:** every owner-only operation is denied to anonymous callers, another user, an Admin, and a caller without the API scope, even with a valid share link. Tests cover forged/archived guest credentials, mismatched list/item/mark/link identifiers, different actors reusing a mark key, malformed/stale row versions, and purchase-free owner serialization. Existing public/private access and owner-precedence tests remain.
- **Client HTTP and orchestration:** tests exercise all transport methods, routes, payloads, current per-request tokens, capability headers, anonymous token avoidance, cancellation, empty responses, and actionable 400/401/403/404/409/500 errors. Guest ownership is persisted before a mark is sent; storage failure prevents an unrecoverable mark. Connection-status tests distinguish readiness, network failure, timeout, and caller cancellation.
- **Owner UI:** component tests cover creation, all item fields, list editing/public-private transitions, removal confirmation and cancellation, item/list archival, authentication-expiry feedback, pending navigation, draft retention, and explicit acceptance of refreshed row versions after conflicts.
- **Viewer UI:** tests cover marking/retry/undo, fulfilled and excess totals, owner redirection, invalid fragments, stale pending responses after navigation, polling, loss of purchase permission with existing own marks, polling disposal, request timeout/cancellation, and refresh failure after a successfully saved mark.
- **Web services and middleware:** tests cover protected guest-storage round trips, deletion, corruption, lost keys, blocked storage, and development-only namespace overrides. Token acquisition verifies the current principal, scopes, cancellation, and interaction-required renewal errors. Integration tests verify local sign-in return paths, no-store/no-referrer responses, cookie sign-out with required antiforgery protection, and startup refusal when authentication is disabled outside Development.
- **Token validation:** a controlled signing-key/metadata fixture exercises the real JWT middleware for valid tokens, invalid signatures, wrong audiences/issuers, expiration, missing scope, and two distinct customer identities. The fixture uses exact local issuer validation instead of Entra metadata discovery and does not disable signature, lifetime, audience, or issuer checks.
- **Real SQL:** tests cover migration, provisioning, persistence, ordering, row versions, stable share-token recovery, persisted revocation/expiration, retained history, competing edits, duplicate retries, and simultaneous different actors/payloads with one idempotency key. A test-only barrier forces both undo requests to read the same version before writing, exercising concurrency recovery. Direct SQL writes prove quantity, price, actor, and foreign-key constraints reject invalid data.
- **Repeatable browser regression:** headless Chromium runs the actual Blazor host over a temporary HTTPS endpoint with separate owner/guest browser contexts and an isolated SQL database. It creates a list/item/link, marks an excess quantity, reloads guest ownership, verifies owner privacy and redirection, undoes the mark, edits the list, revokes the link, and removes the item/list. It also checks browser script errors. The owner cookie and bearer identity are test fixtures; no real Entra credentials are needed.
- **Coverage enforcement:** `coverage-thresholds.json` defines minimum line/branch coverage per assembly. `scripts/Test-Coverage.ps1` rejects missing assemblies, coverage regressions, unsuccessful runs, and skipped cases in the full job. Eight tests verify that gate using synthetic reports.

## Run the full suite

Use PowerShell from the repository root. Restore/build first and install the version-matched headless browser once:

```powershell
dotnet restore ListHero.slnx
dotnet build ListHero.slnx --configuration Release --no-restore
./tests/ListHero.Tests/bin/Release/net10.0/playwright.ps1 install chromium --only-shell
```

Then run all tests and collect coverage:

```powershell
$taskTestDatabase = 'ListHero_Integration_' + [guid]::NewGuid().ToString('N')
$env:LISTHERO_TEST_SQL_CONNECTION = "Server=(localdb)\MSSQLLocalDB;Database=$taskTestDatabase;Trusted_Connection=True;TrustServerCertificate=True"
$env:LISTHERO_BROWSER_TESTS = '1'
try {
    dotnet test ListHero.slnx --configuration Release --no-build --no-restore `
        --settings coverage.runsettings --collect:'XPlat Code Coverage' `
        --results-directory .artifacts/test-coverage --logger 'trx;LogFileName=coverage.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $taskReport = Get-ChildItem .artifacts/test-coverage -Recurse -Filter coverage.cobertura.xml |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    ./scripts/Test-Coverage.ps1 -CoverageFile $taskReport.FullName `
        -TestResultsFile .artifacts/test-coverage/coverage.trx -RequireAllTests
} finally {
    Remove-Item Env:LISTHERO_TEST_SQL_CONNECTION
    Remove-Item Env:LISTHERO_BROWSER_TESTS
}
```

The connection may point to another SQL Server. Test database names must start with `ListHero_Integration_`; each SQL scenario uses a separate suffix. Databases and Git-ignored reports are retained for inspection. Normal development data and browser sessions are not used. The browser's test identity handlers and fixture endpoints exist only in the test assembly and test-host configuration.

## CI and remaining verification

`.github/workflows/ci.yml` retains the Linux build/default-test job and adds a Windows job that starts LocalDB, installs headless Chromium, runs every test, enforces coverage, publishes a coverage table to the job summary, and uploads TRX/Cobertura reports. The workflow and gate have been validated locally; the first hosted Actions run remains to be observed after the repository is pushed.

Live External ID second-account isolation, actual tenant sign-out, and actual token renewal still need a tenant acceptance check. The automated tests verify the application's behavior around those boundaries, and real sign-in/creation has already been checked locally, but they do not prove the external tenant configuration or provider availability. Cross-browser/device coverage, performance/load behavior, and future Azure/MAUI deployment behavior are separate milestones.

Implementation references: [Coverlet collector settings](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/VSTestIntegration.md), [ASP.NET Core test hosting with Kestrel](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.testing.webapplicationfactory-1.usekestrel?view=aspnetcore-10.0), [Playwright installation](https://playwright.dev/dotnet/docs/library), and [Windows runner software](https://github.com/actions/runner-images/blob/main/images/windows/Windows2022-Readme.md).
