# My lists pagination

`/lists` defaults to page 1 with **15 lists per page**. The page-size selector offers **15, 30, 50, and 100**. Changing the selection returns to page 1. Previous/Next links preserve the size, and the URL stores both settings, for example `/lists?page=2&pageSize=30`. Reload and browser Back/Forward keep the selected view. Opening `/lists` without settings returns to the default; no account/browser preference is persisted.

The page and API accept page sizes from 1–100, so a valid custom size in a URL remains visible as an additional selected option. Page numbers are bounded to 1–1,000 to bound offsets and prevent overflow/unbounded deep-page requests. Malformed, duplicated, blank, or out-of-range page/size parameters produce a recoverable invalid-page state in the UI and a 400 from the API. Invalid UI parameters never trigger a list request. Empty later pages offer a link back to page 1 at the same size rather than showing the new-user empty state.

## API and storage

`GET /api/lists/mine?page=1&pageSize=15` returns an `OwnedListPage`:

```json
{
  "items": [],
  "page": 1,
  "pageSize": 15,
  "hasMore": false
}
```

`items` contains the existing list summaries: ID, name, description, public flag, and creation time. Authentication, owner isolation, archived-list/owner exclusion, and no-store responses remain enforced. Purchase data, items, share credentials, and row versions are not included.

This replaces the previous bare-array response and unbounded store read. Application, API, shared client, and reusable Razor UI contracts change together; publish the coordinated release when deployment is authorized. There is no retained unbounded endpoint or compatibility fallback that fetches all lists. Dashboard previews remain independently bounded to five.

SQL applies owner/active-account filters, stable ordering by creation time descending and ID ascending, offset, and a limit of `pageSize + 1`, then projects summary columns. The extra row determines `hasMore`; at most `pageSize` summaries are returned. There is no separate total-count query or exact “page X of Y” promise. Next is not offered at the maximum accepted page.

Offset pagination fits a small personal collection with first-page visits and editable URLs. It is not a frozen snapshot: new lists or archival between requests can shift page boundaries. Future substantially larger workloads can be measured before adding cursor pagination or changing indexes. No model/schema migration is introduced here.

## UI resilience

The reusable `OwnedListBrowser` handles page reads separately from the list-creation form. Changing pages/sizes cancels superseded reads; late responses cannot replace the current page. Re-rendering unchanged parameters does not refetch. Disposal cancels the pending read. Read errors offer retry at the same page/size, and expired login uses existing recovery feedback. Creation keeps its independent validation, pending state, and successful navigation. Existing unsaved-change navigation guards remain active.

## Verification

All 450 tests passed with no skips, including real SQL and headless Chromium, and all nine unchanged coverage gates passed. Release build completed with zero warnings/errors. Evidence is retained in `.artifacts/qol-pagination-complete/coverage.trx` and its coverage attachment.

SQL checks use 105 active lists, tied timestamps, an archived list, and another owner's list. They verify default/maximum sizes, stable nonoverlapping pages, an exactly full final page, empty later pages, and bounded summary-only SQL without entity tracking. API tests reject malformed/duplicate/invalid bounds before user provisioning. UI tests cover defaults, size changes, custom URL sizes, empty/error recovery, cancellation, and stale responses. The browser journey exercises all selector sizes, Previous/Next, Back/Forward, reload, malformed URLs, and first-page recovery against real SQL.

Only synthetic test data was changed. No push, deployment, migration, real-account change, or Azure operation was performed.
