# Archived-account access policy

Disabling an application account means archiving its `User` record. While that account is archived, all lists it owns are unavailable through List Hero, regardless of public visibility, share links, saved access, guest credentials, or the caller's Admin role.

## Behavior

- Other viewers receive the same generic 404 used for unavailable lists. The response does not disclose the account's status, list title, item details, purchase information, or share/guest credentials.
- Viewing, guest issuance/reuse, new purchase marks, idempotent mark retries, own-mark undo, saving, and pin changes all require an active list owner.
- Disabled users continue to receive the existing account-unavailable 403 on authenticated application operations, including owner management, share management, dashboard, and list creation. No admin bypass is added.
- Saved and pinned references remain in recipients' collections as **List unavailable**, with no title or working list/pin actions. Recipients can still remove their own shortcut. Owner-side saved references are also marked unavailable at the query boundary.
- Public list search, people search, and public profiles continue to exclude archived owners. Other active owners' lists remain accessible.
- A viewer page clears its loaded list, title, purchase controls, and transient quantity drafts when a read/mark/undo receives a definitive 403/404. It keeps loaded content on temporary network/server failures so retry remains possible. A late denial for an old route cannot clear a new list.

## Enforcement and retained data

`WishListQueries.ListsWithActiveOwners` applies the list/owner archive predicates inside SQL. Collaboration lookups and owner header/preview reads use that query. Saved-list projections use the same condition before applying owner/public/share eligibility. The application access resolver obtains an eligible list before resolving share/guest/saved access or running purchase operations. Discovery already applies an active-owner predicate.

This is an access rule, not cascade archival or erasure. Lists, items, link tokens, guest identities, marks, undo/idempotency history, and shortcuts are retained unchanged. It introduces no global EF query filter, so maintenance/backup/history queries can still inspect retained records explicitly. No schema migration is required.

Every new API operation reads persisted account status; an existing app cookie, valid API token, saved grant, or bearer link cannot override it. Requests already authorized when disabling occurs may finish, and previously delivered data cannot be recalled from a recipient's browser. There is no new push notification or cross-client content erasure mechanism. Open viewer pages learn of lost access through their next API request, including the existing visibility-aware refresh.

This release does not add an admin account-management screen, account disable/restore endpoint, Entra account suspension, automatic sign-out, or permanent deletion. It enforces the existing application archive mechanism consistently. Re-enabling accounts and deciding whether old links should be revoked on reactivation remain separate lifecycle design work.

## Verification

Tests cover public/private lists, guest credentials, Admin callers, saved private grants, anonymous public access, mark retries/undo after disabling, generic/no-store errors, redacted saved/pinned summaries, direct owner store reads, retained history, unaffected active owners, and removing unavailable shortcuts. Component tests exercise access loss during refresh/mark/undo, recovery by retry, temporary failures, and late responses from an old route. Transport tests verify HTTP status reaches the UI without parsing error text.

All 420 tests passed without skips, all nine unchanged coverage gates passed, and Release build completed with zero warnings/errors. Local evidence: `.artifacts/qol-archived-owner-final/coverage.trx` and its coverage attachment. Only synthetic test accounts/databases were changed; no real user or hosted environment was modified.
