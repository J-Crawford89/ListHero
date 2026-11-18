# Dashboard reads, saved-list concurrency, and viewer polling

Implemented locally November 18, 2026. No deployment, Azure setting change, or database migration is included.

## Dashboard reads

The owned-list preview uses a separate store query that filters by owner and excludes archived lists, orders by creation date descending then ID, selects only summary fields, and applies `Take(5)` before materialization. SQL Server returns at most five rows. The full My lists query is unchanged; pagination there remains separate follow-up work. Saved/pinned previews already use bounded saved-list queries.

## Saved-list writes

Existing shortcuts are updated atomically in SQL. Null pin preferences preserve the current pin; absent explicit share credentials preserve the current saved grant. Pinning from a saved shortcut validates access but does not write back the grant read during validation. A newly supplied share link is saved only when active and scoped to the requested list. Access is checked again on later reads, so saving never makes a revoked/expired link permanent.

Updates to the same explicitly supplied field intentionally use last-write-wins. Independent pin and grant updates merge without reverting each other. Supplying both fields updates them in one statement. Saving a new shortcut without a pin preference starts it unpinned.

If no active shortcut exists, the store attempts one insert. A SQL unique-key collision causes the failed entry to be detached and one atomic update of the winning active shortcut. There is no recursive insert retry. If that shortcut was removed before recovery, the request returns an edit conflict and the user may retry. Other database errors and cancellation propagate normally. Removing a shortcut still archives it; saving after removal may create a new active shortcut.

## Viewer polling

The shared Razor UI owns a JavaScript module that works inside a browser or Blazor WebView. It schedules a read 20 seconds after the previous callback completes while the document is visible and the browser reports online. Hiding the document or going offline cancels the scheduled timer. Becoming visible, coming online, or focusing the window requests an immediate refresh. Overlapping callbacks are suppressed.

An already running read may finish after the page becomes hidden. No new scheduled read starts while hidden/offline. `navigator.onLine` is an availability hint; it cannot guarantee that the API is reachable. Existing API-error behavior remains: a failed list read needs manual retry before automatic API reads continue. Mark/undo requests are never replayed by polling, drafts stay intact, and busy/disposed components skip refresh callbacks.

Component disposal cancels work, removes polling event handlers/timers, and releases JavaScript/.NET references. Navigation between lists discards stale responses. If the polling module cannot initialize, the page explains that manual refresh is available. The existing Refresh list button remains available independently of polling.

## Verification

All 232 tests passed with no skips, including SQL Server and headless Chromium; all nine assembly coverage gates passed. Evidence is retained locally in `.artifacts/qol-performance-final/coverage.trx` and its coverage attachment.

- SQL query capture verifies the five-row limit, summary projection, ordering, filtering, and lack of tracking.
- Coordinated real SQL inserts verify one active shortcut with merged pin/access values. Removing the winning row during duplicate recovery verifies that recovery stops after one insert attempt.
- A paused API pin request verifies that an intervening share-link update survives the older request finishing.
- Component tests verify request serialization, error/manual recovery, polling setup failure, and disposal.
- Browser clock tests exercise the actual module's interval, visibility/online/focus events, in-flight callbacks, disconnected-call recovery, and disposal. The real Blazor journey also verifies that returning to a viewer tab revalidates a revoked share link without manual refresh.
