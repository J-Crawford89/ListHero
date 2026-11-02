# Public list and people discovery — implementation plan

Status: implemented November 2, 2026. Display names plus unique public handles and the rollout rules below were approved during planning. The feature uses the existing SQL Server and shared Blazor UI. The full local suite passes 200 cases, including real SQL queries, profile claim races, and a browser discovery journey; all existing coverage thresholds pass.

## Intended experience

- A **Discover** link is visible when signed in or signed out.
- `/discover` offers **Lists** and **People** tabs with search and pagination. The lists tab can browse recent public lists without a search term. The people tab searches public display names and handles; with an empty query it shows guidance rather than an account directory.
- Searching for a person returns their chosen display name and `@handle`. Selecting the result opens `/people/{handle}`, showing all of their currently public lists across pages.
- List cards show a name, short description, and public author identity. They link to the existing `/view/{listId}` page without a share token.
- The existing viewer remains responsible for list access, purchase marks, fulfillment, and redirecting a signed-in owner to their ordinary owner view.
- A signed-in **Public profile** page at `/profile` lets someone choose their public name and unique handle. Private-list-only users do not need to complete it.

This release does not add followers, friends, favorites, item search, categories, profile photos, locations, email lookup, or search-engine indexing work.

## Findings in the current code

| Existing implementation | Implication |
| --- | --- |
| `WishList.IsPublic`, `OwnerId`, and explicit archive filtering | The ownership and visibility relationships already exist. No replacement sharing system is needed. |
| `ListAccessService`, `ListAccessResolver`, and `ListViewerService` | Public viewing, authenticated purchase marking, bearer-link access, and owner precedence are already enforced. Discovery must use these existing detail routes. |
| `ViewerList.razor` at `/view/{ListId:guid}` | A public list already works without a URL fragment; signed-in owners are redirected to `/lists/{id}`. |
| Separate owner/viewer DTOs | Search summaries must be another explicit, minimal projection, with no purchase data. |
| `User.DisplayName` populated from an Entra name claim on first creation | This is an identity-derived name, not a chosen public profile. Existing users are returned unchanged by `EfUserStore`; later Entra name changes do not update that stored value. |
| No public profile or user search capability | Add application-owned public identity; do not search Entra or expose issuer/object ID/login details. |
| `EfWishListStore` only provides owner-oriented list queries | Add a dedicated discovery read capability rather than retrieving all lists and filtering in the browser. |
| `OwnerList.razor` says public lists are visible to anyone with the address | Update this explanation and handle existing public lists deliberately when introducing search. |
| API has a default authenticated fallback policy; no-store currently covers `/api/lists` and `/api/session` | Explicitly mark discovery reads anonymous, protect profile writes, and extend cache controls to the new endpoints. |

## Public identity and visibility rules

### Profile

Add nullable `PublicDisplayName`, nullable `PublicHandle`, and a SQL row version to the existing `User` entity. Keep `DisplayName`, `IdentityIssuer`, and `IdentitySubject` separate and out of public DTOs. A separate profile table is unnecessary for this small one-to-one relationship.

- Public display name: required when publishing a profile, trimmed, 1–100 characters; duplicates allowed.
- Handle: 3–30 lowercase ASCII letters, digits, underscores, or hyphens, starting with a letter or digit. Normalize before validation/storage and enforce uniqueness in SQL, including concurrent claims.
- Use a filtered unique index on non-null handles. Private accounts without a profile must coexist normally.
- Allow display-name edits. For v1, a handle cannot change after it is claimed; explain this in the form. Renames, aliases, handle reuse, and redirect management can be designed later.
- Do not automatically generate handles from email or publish an identity name. The profile form may suggest the current name, but the user confirms what becomes public.
- Profile updates use the same row-version conflict pattern as list edits. A handle conflict returns a useful 409 response without identifying the other account.

### Discoverability

A list is discoverable only when the list is public and unarchived, its owner is unarchived, and its owner has completed their public name/handle. These predicates apply to every search, profile listing, and any result counts—not just to the final page of results.

- Public profiles appear in people search and public profile reads only while they have at least one discoverable public list. A private-only account is not listed.
- A missing, archived, or non-discoverable public profile returns the same unavailable/404 result. Do not reveal private list titles, private list counts, or account email addresses.
- Changing a list to private or archiving it excludes it from subsequent discovery reads. Opening an old search result still goes through the existing access resolver and can fail appropriately.
- Discovery summaries never query purchase records or include fulfillment, reserved quantities, buyer information, share-link tokens, row versions, or login identifiers—even for the owner.
- The existing authenticated owner precedence and purchase permissions stay in force when a result is opened. Discovering a public list does not give anonymous visitors permission to mark gifts.
- Existing list access and admin permissions are not expanded by this feature. Discovery filters archived owners; a broader account-suspension/content-access policy remains separate from this work.

### Existing public lists and first publication

Recommended rollout: leave all existing list visibility values unchanged. Existing public URLs continue to work, but their lists are not indexed in discovery until the owner completes public profile setup. Explain clearly that completing the profile publishes the selected name/handle alongside **all** of that owner's public lists.

When changing a private list to public for the first time, require a completed public profile and show: “Anyone can find this list in search and on your public profile.” Existing public lists without a profile must still allow ordinary edits and archiving; do not accidentally block their maintenance. Completing the profile makes their existing public lists discoverable automatically.

Once the profile is set up, `IsPublic` is the only per-list visibility choice: public means discoverable; private requires existing ownership/share access. Do not add a second per-list search toggle in v1. A separate public-but-unlisted mode could be designed later if wanted.

## API and project boundaries

Proposed routes:

| Route | Access | Result |
| --- | --- | --- |
| `GET /api/discovery/lists?q=...&page=1&pageSize=20` | Anonymous | Public list summaries, public author identity, paging metadata |
| `GET /api/discovery/people?q=...&page=1&pageSize=20` | Anonymous | Public name/handle summaries for people with public lists |
| `GET /api/discovery/people/{handle}?page=1&pageSize=20` | Anonymous | Public profile header and a page of that person's public lists |
| `GET /api/profile/me` | Authenticated standard user | Own editable profile and row version |
| `PUT /api/profile/me` | Authenticated standard user | Validated saved profile or conflict |

Use separate endpoint groups: discovery must explicitly override the API's authenticated fallback policy. Profile ownership comes from the validated caller and `IUserService`, never a caller-supplied user ID. Public discovery reads are identity-independent and do not need to provision an internal user. Reuse the application's existing authentication boundary conventions where credentials are supplied.

| Project | Proposed additions |
| --- | --- |
| Domain | Public-profile invariants/methods on `User`; row version |
| Contracts | Discovery query/paging contracts, `PublicAuthorSummary`, `PublicListSummary`, `PublicPersonSummary`, public-profile response, private own-profile request/response |
| Application | `IDiscoveryService` / `DiscoveryService`; `IDiscoveryStore`; `IPublicProfileService` / `PublicProfileService`; a profile persistence interface |
| Infrastructure | EF discovery projections and profile writes, indexes, additive migration |
| Api | Discovery/profile endpoints, validation, authorization, no-store and bounded request handling |
| Client | `IDiscoveryApi` and `IPublicProfileApi` capabilities |
| Client.Api | Typed HTTP implementations; discovery reads work without acquiring a token, own-profile calls use the existing token provider |
| UI | Discover, public person, and own-profile pages; result cards; navigation and publication copy |
| Web | Existing routing/composition and new service registrations only; shared UI remains usable by a future MAUI host |

Keep discovery separate from `IWishListApi` owner CRUD and from the existing collaboration API. Add a client orchestration service only if a real reusable workflow needs it. No generic repository or mediator layer is needed.

## Query behavior and SQL considerations

- Default page size 20, maximum 50; validate page bounds and cap maximum offset. Fetch one extra row for `HasMore`; do not add an expensive global count just to render pagination.
- Empty list query browses recent public lists. Nonempty queries must be 2–100 characters after trimming; an empty people query does not scan every user.
- Search list names/descriptions and public names/handles. Match handles exactly first, then use a documented simple matching order. Treat input as literal text, including SQL wildcard characters; use EF parameterized queries.
- Use an explicit case-insensitive search policy. Handle normalization/uniqueness must not depend on the incidental SQL database collation. Accent-insensitive matching is optional and should be decided before locking search expectations into tests.
- Use deterministic ordering, such as created time descending plus ID for list browsing, and match rank/public name/handle for people. Cap deep paging; concurrent edits can move entries between pages, so v1 does not promise a snapshot or a relevance engine.
- Apply public/active/profile predicates and owner restriction before paging and projection. Use `AsNoTracking`, cancellation tokens, and server-side minimal DTO projection; never materialize all lists, items, or users first.
- Start with normal SQL Server queries and an index supporting active public-list browsing; retain/review the existing owner/archive index for person pages. Measure query plans with representative data before adding duplicate or wide indexes. Substring search may scan candidate rows; a normal index does not turn it into full-text search.
- No Azure AI Search, external search service, or new paid infrastructure is proposed. The additive profile/index migration goes through the existing migration job and must coexist with the previous app during deployment.
- Bound public search requests, avoid automatic polling, and use submit-based search initially. Add a modest per-instance read rate limit if needed for the exposed endpoints; it is not a distributed abuse-control guarantee.

## UI behavior

Use the existing comic-themed components without broadening this task into a redesign. Search fields and tabs need accessible labels, keyboard behavior, loading/empty/error states, and mobile layouts. Keep the query/tab/page in the URL so back navigation restores the results. Cancel outdated requests or ignore stale responses after navigation/search changes.

Public author links always use the selected handle. Avoid logging entire search text by default; someone may type a person's real name. No purchase totals or fulfillment badges appear on discovery cards or profile summaries. Keep new endpoints and pages free of shared response caching in v1 so fresh reads follow visibility changes; already displayed public information cannot be recalled from a visitor's browser.

## Implementation sequence and acceptance

1. **Public profile foundation:** additive nullable fields, filtered handle uniqueness, row version, domain validation, authenticated profile service/API/client/UI, and concurrency handling. Existing accounts, lists, links, and identity mapping continue working.
2. **Discovery queries and endpoints:** public-only SQL projections, list/people search, paginated person profiles, anonymous access, and bounded inputs. Verify exclusion rules against real SQL before exposing navigation.
3. **Shared UI integration:** Discover navigation/pages, public author links, one-time profile setup messaging, and revised public-list publication behavior. Reuse `/view/{id}` rather than adding another item viewer.
4. **Regression and rollout:** run existing unit, API, client, component, real SQL, browser, and per-assembly coverage checks. Deploy through CI/CD, then check signed-out discovery and a two-account journey on the beta. Review the profile/publication wording before announcing discovery to existing users.

Essential new coverage:

- Private/archived lists and archived/private-only users never leak through results, snippets, paging metadata, or profile reads; an owner-only or private share link does not make something searchable.
- Same-name people are distinguishable by handle; handles normalize, validate, reject concurrent duplicate claims, and cannot be renamed accidentally. Profile writes require the correct authenticated user and enforce row versions.
- Real SQL verifies translation, case handling, literal wildcard input, public-only ordering/paging, and the filtered nullable unique index. Do not substitute an in-memory database for these checks.
- Public-to-private changes, archiving, the last public list becoming private, and legacy profile onboarding affect the next discovery/profile reads correctly.
- Responses contain no purchase state, login identifiers, private list counts, or bearer credentials. Opening one's own result still returns/redirects to the purchase-free owner view.
- Signed-out users can browse/open public lists but cannot mark without signing in or possessing a valid share link. Existing private-share and undo workflows remain intact.
- Client/component tests cover optional authentication, duplicate-handle errors, pagination, empty states, back navigation, and stale-request handling.
- Browser regression: set up an owner profile and public/private lists, find that owner by name and handle from a second context, open a public result, verify owner privacy, then make it private and verify it disappears and the old direct route denies access.

First-release success means someone can search for their spouse, recognize the chosen name/handle, open the spouse's profile, and browse every public list without ever receiving a share link or seeing evidence of private lists.

## Build maintenance included with implementation

Restore exposed security advisories in existing transitive dependencies. Explicit references select patched `System.Security.Cryptography.Pkcs` and `System.Security.Cryptography.Xml` 10.0.13; audit checks remain enabled. See Microsoft's [PKCS advisory](https://github.com/advisories/GHSA-846f-f764-76mx) and [XML advisory](https://github.com/advisories/GHSA-7hqp-7c5m-4vq6). These changes are separate from the discovery data model and do not add a service or paid dependency.
