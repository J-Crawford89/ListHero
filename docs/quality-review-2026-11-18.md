# Project quality and everyday usability review

Reviewed November 18, 2026. This is a static review of the solution's routing, authentication, authorization, persistence, client adapters, shared UI, tests, and deployment scripts. Findings below distinguish confirmed code behavior from suspected causes requiring browser/runtime evidence. No application settings, identity policies, or production behavior were changed. DinnerDial was not reviewed; a similar symptom there does not establish the same cause.

## Follow-up implementation status

The findings below record the original review. Four follow-up batches have since been implemented locally; none has been pushed or deployed:

- Session persistence and navigation: commit `8fe9141` adds remembered login, session renewal/revalidation, smart root routing, `/welcome`, and protected sign-in return navigation. See [session-navigation.md](session-navigation.md) for policy, validation, and limitations.
- Saved-list and profile editing: saving/updating access preserves the existing pin unless a pin preference is explicitly supplied. New shortcuts start unpinned. Save confirmations survive unchanged parent refreshes, and late responses cannot show success for a different list/access context. Profile conflicts allow comparison with the saved version, explicit reuse of the latest version while keeping the draft, or intentional discard. Reloading a modified profile requires confirmation. Handles already claimed in another session remain immutable.
- Dashboard reads and viewer polling: owned previews now project at most five summaries in SQL. Saved-list inserts have one bounded duplicate-recovery update, and atomic preference/access updates preserve fields not explicitly supplied. Browser polling pauses in hidden/offline tabs, refreshes on return, and cleans up on component disposal. See [dashboard-performance.md](dashboard-performance.md) for concurrency policy and polling behavior.
- Route and response recovery: discovery, public profiles, and saved lists validate page queries before calling the API and offer first-page recovery. Repeating a search at its existing URL refreshes results, stale reads cannot replace a new route, and saved lists handle expired sign-in with a recovery link. A shared HTTP response reader handles malformed/empty payloads and optional error details while preserving status-specific recovery. Unreadable write responses warn that the request may already have succeeded. See [route-response-resilience.md](route-response-resilience.md).

Validation after the second batch: all 226 tests passed with no skips, including SQL Server and headless-browser tests. All nine assembly coverage gates passed. Local evidence: `.artifacts/qol-workflow-verified/coverage.trx` and its coverage attachment. Neither batch requires a business-data schema migration. General navigation/reauthentication draft preservation remains follow-up work.

Validation after the third batch: all 232 tests passed with no skips, including deterministic SQL save races and headless-browser polling checks. All nine assembly coverage gates passed. Evidence: `.artifacts/qol-performance-final/coverage.trx` and its coverage attachment. This batch requires no schema migration.

Validation after the fourth batch: all 311 tests passed with no skips, including SQL Server and browser route/search recovery. All nine assembly coverage gates passed. Evidence: `.artifacts/qol-resilience-final/coverage.trx` and its coverage attachment. This batch requires no database migration or Azure/Entra configuration change.

Next batch: cross-tab session regression coverage and dirty-form navigation guards. Redacted operational diagnostics, retention/restore work, and the archived-owner policy decision remain follow-up items; the UI/UX review is still deferred.

## Overall assessment

Keep the current modular solution. Domain invariants, application authorization, persistence interfaces, separate HTTP clients, and reusable Razor UI are appropriate for this app. A rewrite, microservices, a generic repository layer, or a widget engine would add complexity without resolving the present friction.

Strong existing boundaries include owner/viewer response separation, server-side ownership/scope checks, share-link revocation and expiration, separate guest ownership credentials, account-scoped saved references, optimistic concurrency for content/profile edits, purchase idempotency, restrictive foreign keys, encrypted hosted key persistence, encrypted SQL token caching, antiforgery on logout, and CI SQL/browser/coverage gates. These are foundations to preserve, not evidence that every runtime/session edge case has been tested.

## 1. Sign-in persistence and apparent logout — first priority

### Confirmed configuration

`src/ListHero.Web/Program.cs` configures Microsoft Identity Web, token acquisition, and distributed token caching. It does not explicitly configure the app cookie lifetime, sliding renewal policy, or persistent login. The custom sign-in challenge sets a redirect destination but does not set `AuthenticationProperties.IsPersistent`. Do not confuse configured persistent Data Protection keys with a persistent browser login: keys allow a cookie to be decrypted after restart; they do not make the browser retain that cookie.

`WebApiAccessTokenProvider` resolves the circuit's principal explicitly and asks MSAL for a token. Challenge-required exceptions become `SignInRequiredException`. This is sensible separation, but the UI turns recovery into a manual click and loses most navigation context.

### Likely explanation, not yet a reproduced diagnosis

There are three independent pieces: List Hero's login cookie, Entra's own browser session, and the downstream API token/cache. List Hero can lack a valid app cookie while Entra still recognizes the browser. Clicking Sign in then performs an OIDC round trip without another visible credential prompt. This fits the reported symptom; it does not by itself indicate an authentication bypass or a UI rendering bug.

Microsoft documents [app-cookie persistence](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-10.0) separately from [External ID browser sessions](https://learn.microsoft.com/en-us/entra/external-id/customers/concept-supported-features-customers). Browser session restoration can complicate observations, so closing a tab is not equivalent to ending a browser session.

A signed-in circuit can also retain its authentication state while token acquisition later requires interaction. The current code has no custom periodic session revalidation or explicit browser cookie-renewal mechanism. SignalR interactions and server-to-server API calls should not be assumed to renew the browser's app cookie. Hosted cache/key persistence is already implemented; app sleep alone should not be blamed without checking evidence. Local development uses an in-memory token cache and can behave differently after a restart.

### Recommended policy and diagnostics

- Offer “Remember this device” with an explicit persistent-cookie policy. A proposed starting point is 30 days of inactivity and a separately enforced 90-day absolute session ceiling; these are product choices, not promises that Entra will never require authentication sooner. Keep an appropriate nonpersistent/shared-device option.
- Keep API tokens short-lived; let MSAL refresh them. Do not put API or refresh tokens in browser local storage, disable validation, or equate token expiry with mandatory logout.
- Renew cookies via a real HTTP response when appropriate. If active-circuit renewal is required, base it on actual user activity; an unattended polling tab must not keep someone signed in forever.
- Revalidate long-running circuits and handle sign-out in another tab consistently. Avoid redirect loops or automatic replay of writes when interaction becomes necessary.
- Add safe diagnostics: authentication outcome, ticket issued/expiry times, persistence flag, cache hit/miss category, interaction-required category, deployment/instance correlation. Never record cookie values, bearer/share/guest tokens, client secrets, or raw MSAL cache entries.
- Inspect effective deployed options and the authenticated response's cookie expiry attributes, plus a sanitized redirect trace. Check Entra tenant session policies independently before claiming a root cause.

Acceptance checks: browser restart, return after days, API token refresh, app restart/redeployment, long-running tab, sign-out across tabs, and explicit tenant/session revocation. Use shortened lifetimes with a controlled clock for automated tests, then a real Entra browser acceptance check.

## 2. Entry routing and return destinations — first priority

**Confirmed:** `Home.razor` renders marketing at `/` for both authentication states. Ordinary sign-in returns to `/dashboard`. Recovery links in `RequestFeedback.razor` supply no current destination; the sign-in endpoint accepts only `listId`, so profile/saved-list/editor context is lost. The shared viewer supplies a list ID for its own sign-in link, but URL-fragment share keys are not preserved by that ID.

Recommended routes:

| Route | Behavior |
| --- | --- |
| `/` | Signed in: temporary redirect to `/dashboard`; anonymous: marketing |
| `/dashboard` | Protected application landing page |
| `/welcome` | Explicit marketing page accessible to everyone |
| Protected deep link | Sign in and return to the intended safe local destination |

Keep `/dashboard` stable rather than moving the entire app to `/`. Handle initial requests before rendering to avoid a marketing flash, and make navigation links agree with the policy. Authentication-dependent redirects/responses must not be shared-cached or permanently redirected.

Support validated local return destinations, not arbitrary external URLs. Preserve drafts when reauthentication is required. Preserve a pending private-share context through a protected, short-lived host storage flow; do not move raw fragment secrets into query strings or logs. Clear that context after recovery, and still recheck current share validity. This flow also makes it easier for an anonymous recipient to sign in and save a private list.

## 3. QoL defects and focused follow-up checks

| Priority | Finding and evidence | Recommended correction |
| --- | --- | --- |
| High | Saving an already pinned list from the viewer sends `Pin=false`. `SaveListButton` defaults to false, and `EfSavedListStore.SaveAsync` overwrites pin state. | Separate “save/update access” from “set pin state,” or make pin changes explicitly optional. Saving must preserve an existing pin. |
| Medium | `SaveListButton.OnParametersSet` always clears its success flag. Parent viewer refreshes can reset the confirmation without changing the list. | Reset only when list/access context changes; ideally load the actual saved/pinned state. |
| Medium | `PublicProfile.razor` reload replaces the entire draft, while `PublicProfileApiClient` uses the generic conflict message saying unsaved changes remain. | Give profile conflicts an explicit “load latest version, keep my draft” flow, or accurately warn before discarding it. |
| Medium | The UI's numeric query parameters are converted before API validation. Manually malformed `page=abc` URLs need a friendly route-level fallback rather than relying on API bounds checks. | Validate route/query parsing and render a recoverable invalid-page state. Add browser tests. |
| Medium | Discovery submits by navigating to the same query URL. A repeat submission may not refresh the component when parameters are unchanged. | Explicitly reload on identical submissions; test results changing between searches. |
| Medium | Protected-route `NotAuthorized` content only says sign-in is required. | Provide a clear recovery action with the safe return destination; distinguish access denied from expired login. |

## 4. Data access, concurrency, and cost

- **Confirmed:** `DashboardService.GetAsync` loads all owned lists through `GetByOwnerAsync`, then takes five in memory. Return size is bounded but the SQL query is not. Add a bounded read projection for dashboard previews; paginate My lists independently as collections grow.
- **Confirmed:** `EfSavedListStore` retries duplicate inserts recursively without a retry bound. Replace this with a bounded winner-reload/update path and a concurrent-save test. Pin/access updates currently use last-write-wins; define whether that is intentional and prevent a pin-only action from overwriting a concurrent grant update.
- **Confirmed:** viewer polling runs every 20 seconds while the component exists, including background tabs, and a refresh fetches the full list/marks. Pause on hidden/offline pages, resume on focus, and consider change versions or backoff. This matters on the existing free hosting/SQL allowances.
- **Policy gap:** discovery hides archived owners, but ordinary list access checks the list and current viewer rather than the list owner's account status; saved-summary availability also lacks that owner check. Decide whether disabling an account should disable its shared/public content, then enforce that consistently before admin operations expand. This is not a confirmed violation of an agreed suspension policy.
- Define retention/cleanup for old isolated test databases, expired guest identities, archived shortcuts, and token-cache rows. Do not automatically delete retained business history. Keep cleanup scoped and reviewable.

## 5. Client/UI resilience and maintainability

- HTTP adapters translate status errors but generally assume valid JSON on successful responses; some error parsing also assumes JSON. Add a consistent transport helper for empty/malformed bodies, temporary service outages, and correlation IDs, while retaining distinct permissions/conflict messages. Avoid a generic abstraction that hides each workflow's rules.
- Add a navigation guard for dirty forms. Drafts survive some API conflicts today, but navigating away or performing full-page reauthentication can still lose them. Keep private drafts protected and short-lived if persisted.
- Do not let one optional future dashboard section take down the entire dashboard. Lists should remain useful when recommendations or sponsored content fail.
- Clarify “remove shortcut” versus “archive list,” provide saved/pinned feedback, and add contextual empty states. Keep manual pin ordering and people connections as explicit future features, not assumed current capabilities.
- Expand new one-line implementations into readable methods and split mixed contracts/interfaces/implementations when that improves reviewability. Keep the current project boundaries; avoid architecture churn just for style.

## 6. Operations and test strategy

The existing automated suite and coverage gates are valuable, but do not currently establish weeks-long browser persistence, deployed Entra session behavior, or real cross-tab sign-out correctness. Add those targeted tests rather than increasing the coverage percentage for its own sake.

Add lightweight, redacted operational signals for sign-in recovery, token-cache failures, API/database cold starts, and failed deployments. Keep paid infrastructure opt-in. Monitor upcoming client-credential/certificate expiry, exercise database/key-ring recovery, and retain the free-tier deployment checks. The recent CLI field rename illustrates why deployment guards need compatibility tests and clear diagnostic reasons.

## Suggested work order

1. Instrument and reproduce authentication/session behavior; agree and implement persistent-login, renewal, and revalidation policy.
2. Smart root routing, explicit marketing route, safe return destinations, and private-link/draft-preserving recovery.
3. Fix save-versus-pin behavior, unstable save feedback, and profile conflict recovery.
4. Bound dashboard queries, replace recursive retries, and make polling visibility-aware.
5. Route/error resilience, cross-tab session tests, lightweight observability, cleanup/restore drills, and then the planned full UI/UX review.

No implementation or deployment is included in this review.

Dependency check: `dotnet list ListHero.slnx package --vulnerable --include-transitive --format json` completed on the review date and reported no known vulnerable packages across the solution. Evidence is retained in Git-ignored `.artifacts/qol-package-audit.json`. This checks published package advisories, not runtime configuration or all possible vulnerabilities. Functional tests were not rerun because this review changed documentation only; the previous 203-case result is not a new execution claim.
