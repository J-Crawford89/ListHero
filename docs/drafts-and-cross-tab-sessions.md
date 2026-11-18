# Unsaved drafts and cross-tab sessions

Implemented locally November 18, 2026. No push, deployment, database migration, Azure setting, or Entra policy change is included.

## Navigation guards

The shared Razor UI tracks explicitly marked business forms: new lists, list edits, item creation/edits, public profiles, share-link expiration choices, and viewer purchase quantities. Field values are compared with an in-memory baseline, including raw input that has not yet blurred or passed validation. Hidden antiforgery fields and submit controls are excluded. Search filters, read-only share addresses, and ordinary browsing do not count as unsaved business changes.

A layout-level location-changing handler asks before internal navigation, including history navigation. A native `beforeunload` guard covers reloads, tab closure, and full-page navigation when the browser permits a prompt. Canceling keeps the current page and its draft. Returning fields to their baseline removes the warning. Successful writes reset the appropriate baseline; failed validation, API failures, and edit conflicts do not. Explicitly canceling an editor or accepting profile discard removes that draft. Pending add-item fields remain in the DOM while an item editor is open, so opening/canceling an editor does not lose an unfinished new item. Switching item editors checks only the item draft being replaced.

Known successful list creation/archive operations acknowledge their forms before programmatic navigation. Shared JavaScript state coordinates sign-in/session recovery with the unload guard to avoid a second prompt for an already approved reload. Declining sign-in creates no pending-return cookie. Accepting still uses the existing encrypted, antiforgery-protected private-share return flow.

Draft baselines live only in page memory; they are not written to browser storage, the database, URLs, or logs. These are navigation warnings, not autosave or draft restoration. Accepting reload/navigation discards the draft. Browser/OS termination can bypass unload prompts. The UI reports initialization failure so manual saving remains possible.

## Session synchronization

The existing visible/activity-based, antiforgery-protected session check remains bounded to five-minute intervals, with an immediate check on window focus. It compares the page's original opaque session ID with the current cookie session and detects invalid antiforgery context after account changes.

When a change is detected, the shared guard marks the circuit-scoped client session as requiring sign-in before offering reload. The web API token provider then refuses authenticated token acquisition for that old circuit. This prevents newly initiated authenticated API calls from continuing under the previous identity while a user copies their draft. Requests already in flight may finish; this is browser/circuit synchronization, not global JWT revocation.

Declining reload leaves the original session baseline intact, preserves the draft on screen, and shows a persistent reload action. Later focus checks still offer recovery. Accepting reload creates a fresh circuit from the current cookie; no rejected write is replayed. A disconnected observer does not prevent another observer from receiving the session-change notification. An unchanged session must not reload repeatedly.

## Verification

All 316 tests passed without skips, including SQL Server and headless Chromium. All nine assembly coverage gates passed. Evidence: `.artifacts/qol-session-drafts-final/coverage.trx` and its coverage attachment.

- Real browser tests cover canceled internal links, Back, reload, tab close, and sign-out; accepting navigation; restoring the original field value; unguarded search filters; successful list/item/profile/share-link saves; and profile discard.
- Dirty private-share sign-in is canceled without losing the quantity or creating return state. Acceptance produces one confirmation and preserves the encrypted return flow.
- Two tabs share actual app cookies. Tests replace the signed-in account, decline reload twice, retain the draft, and verify that submitting it cannot create a SQL list from the stale circuit. Recovery reloads into the new session. Additional checks cover sign-out in another tab, anonymous-to-signed-in synchronization, and an unchanged session avoiding a reload loop.
- Component/service tests cover navigation cancellation, editor switching, initialization fallback, scoped session invalidation, disposal, and refusing MSAL acquisition after a detected session change.

The browser fixtures use real app cookie/antiforgery middleware and SQL with test-only identity setup; they do not contact Entra or prove provider-specific policies or weeks-long browser retention. Operational diagnostics, retention/restore policy, and optional protected draft restoration remain separate work.
