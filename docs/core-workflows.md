# Core workflows

## Owner

Sign in at `https://localhost:7016/`, open My lists, and create or reopen a list. Add a wish using all agreed fields. Edit list changes its name, description, and public/private visibility. Edit item supports every item field, with a limited warning when active purchase marks exist. Edits remain allowed after that warning.

Remove actions ask for confirmation and archive records. Archiving a list makes its items and links inaccessible through ordinary routes without deleting their history. Archiving an item hides it from owner and viewer pages and prevents further marking/undo through ordinary routes. Its purchase history remains attached.

Every edit/archive carries the displayed SQL row version. A stale version receives 409 and leaves the draft intact. Reload saved list to compare current values. If applying your draft is still appropriate, choose Use refreshed version and keep my draft, then save. That is an explicit decision to replace the reviewed fields; another intervening change still receives 409.

## Sharing and viewing

Share this list creates independent bearer links with optional UTC expiration. An active, unexpired link is recovered unchanged when the owner reopens the page. Revoke link invalidates that link without rotating others. Expired/revoked links remain listed but their token is no longer delivered. There is no product-level link-count cap.

Private address example: `https://localhost:7016/view/{listId}#{opaqueToken}`. Copy the entire address, including the fragment after `#`. Fragments are not sent in normal HTTP request URLs; the interactive client forwards the token in a dedicated API header. Referrer policy is no-referrer, and list/viewer/API responses use no-store. Do not add request-header/body logging that exposes share keys, guest credentials, authorization headers, or owner link-management responses.

Public lists expose the same viewer path without a token. Anonymous public viewers can see fulfillment but cannot mark or undo gifts without signing in or possessing an active share link. Public sign-in returns to the selected list. A signed-in owner opening any viewer URL is sent to the owner page; the API never includes purchase fields in that owner's response. An expired signed-in session prompts reauthentication rather than downgrading to anonymous.

`IListShareUrlBuilder` keeps public share addresses separate from a host's internal routing address. The Web host uses `Sharing:PublicWebBaseUrl` when configured, otherwise its navigation base URL. A future MAUI host supplies the deployed public Web address to the same builder rather than generating device-local share addresses.

## Purchase marks and guests

Viewers with permission choose a positive quantity and Mark as purchased. Overpurchasing is accepted; totals show fulfillment and any excess. Totals refresh immediately after local actions and every 20 seconds for other viewers; Refresh list also reloads them.

The API records exactly one actor per mark: a signed-in user or a validated guest. Guest ownership is separate from list access. Before an anonymous mark, the client obtains/reuses a guest credential and persists it in protected browser local storage. If storage cannot be written, no purchase mark is sent. Reloading preserves ownership while that credential and the Web host's protection keys remain available. Clearing storage, switching browsers/devices, or losing keys can prevent recovery; there is no guest-account recovery flow yet.

Only the mark's actor can undo it. A valid guest credential can establish ownership after signing in, but anonymous undo still requires an active share link. A share link never grants ownership of everyone else's marks. Buyer IDs are not exposed to other viewers; only the caller's own active marks are returned for undo controls.

A mark request has a generated idempotency key. Retries keep that key and quantity; the UI locks the quantity after an attempted submission until it succeeds. A successful response resets the form for another intentional mark. Repeated requests return the original mark, including an already-undone mark, instead of creating or resurrecting it. A reused key with another actor/quantity returns 409. SQL uniqueness handles simultaneous retries. Undo is idempotent and uses row versions to prevent lost updates.

## Verification

Release builds with zero warnings/errors. The expanded suite contains 150 tests, including five SQL scenarios and a repeatable headless browser journey. Checks cover every owner operation's permission boundaries, token validation, stale edits, archival, link scope/revocation/expiration, scope enforcement, owner privacy, guest/user undo, overpurchase totals, actor/payload-sensitive idempotency, SQL constraints and races, client transport/storage sequencing, draft retention, shared forms, protected browser storage, and sign-out antiforgery. See [coverage and full-suite instructions](test-coverage.md).

An isolated browser test used ports 7336/7443 and a separate `ListHero_Integration_*_Core` database. It verified anonymous marking, a quantity of two, fulfillment/excess totals, guest ownership after reload, and undo. Browser input used normal keyboard events to commit number-field changes. The normal development user's saved list and item were not changed.

To reproduce the isolated browser check, set `LISTHERO_UI_FIXTURE_PATH` to `.artifacts/ui-fixture.json` while running the SQL tests. `scripts/Start-Local.ps1 -Smoke` reads that fixture, isolates its login cookie and guest storage, and writes the test address to `.artifacts/smoke-url.txt`. `scripts/Stop-Local.ps1 -Smoke` stops only those recorded test hosts. Test credentials/files remain Git-ignored. Ordinary runs do not create a browser fixture.

Production readiness still needs the live Entra sign-out/renewal/second-customer checks, Azure environment configuration, persistent protected Data Protection keys, shared token caching, and deployment/migration review. Hosting remains local. The API never accepts test identity headers outside TestServer.
