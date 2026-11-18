# Route and HTTP response recovery

Implemented locally November 18, 2026. No push, deployment, database migration, or Azure/Entra configuration change is included.

## Page URLs and repeated searches

Discovery and public-profile list pages accept a single page number from 1–100; saved lists accept 1–1000. A missing page defaults to 1. Empty, malformed, repeated, overflowing, and out-of-range values render a recoverable message before any API call. The first-page link preserves the discovery search/tab or public handle. API-side validation remains in place.

Page query parameters bind as strings instead of converting directly to integers inside Blazor's parameter binder. Route changes cancel/invalidate pending reads and clear stale page state. A late response or error cannot replace a newer page, including an invalid-page view. Submitting a search whose target URL is already current explicitly reloads its results without relying on navigation to rerun parameter binding.

The read-page base now handles `SignInRequiredException`. Saved lists offer the shared sign-in recovery link and retry action instead of letting expired authentication escape the component. Recovery uses the existing host sign-in/return-location flow. Network errors use a page-neutral message rather than referring to discovery on every page.

## HTTP response handling

All six HTTP adapters use a shared reader for required JSON responses. It translates empty/no-content, malformed JSON, unsupported encodings, and deserialization failures into controlled API errors. Constructor requirements and nullable annotations are checked during deserialization. Optional owner/viewer response sides remain optional, matching the server's existing omission of null sides; the JSON wire format is unchanged.

Known status handling stays in each workflow adapter: sign-in, permissions, unavailable resources, edit conflicts, handle conflicts, and profile setup retain their existing messages/actions. Optional problem codes are read defensively, so empty, malformed, scalar, or wrong-type error bodies fall back to the status-specific message. Raw payloads and parser details are not displayed. Legitimate no-content command responses remain successful; a no-content response to a model-returning operation is an error.

Cancellation is not converted into a parsing error. Status checks still report an unavailable API through the connection-status service. The reader introduces no retries. If a successful write has an unreadable required response, the UI warns that the request may already have succeeded and asks the user to check the result before retrying. Existing drafts and purchase idempotency keys are retained by their workflows.

## Verification and remaining scope

All 311 tests passed with no skips, including SQL Server and headless Chromium. All nine assembly coverage gates passed. Evidence is retained locally in `.artifacts/qol-resilience-final/coverage.trx` and its coverage attachment.

Component tests cover invalid query ranges/duplicates, first-page recovery, unchanged searches, cancellation/stale responses, and saved-list sign-in recovery. HTTP tests cover every adapter, unreadable success/error bodies, required fields, nullable owner/viewer shapes, unsupported encodings, and writes not being replayed. The real Blazor journey follows recovery links on all three page types and repeats a people search after the owner's last public list becomes private.

Cross-tab session regression coverage, general dirty-form navigation guards, correlation/operational diagnostics, and retained-data cleanup/restore policy remain separate follow-up work.
