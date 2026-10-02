# List Hero architecture and accepted rules

## Structure

One repository, one .NET 10 solution, one backend with explicit boundaries. No microservices, message bus, event sourcing, mandatory mediator, or generic repository wrappers are needed for the initial scope.

| Project | Responsibilities | References |
| --- | --- | --- |
| Domain | Entities and local invariants | None |
| Application | Backend use cases, authorization rules, response projection, persistence interfaces | Domain, Contracts |
| Infrastructure | EF Core/SQL Server stores and migrations, capability-token cryptography | Application |
| Api | HTTP boundary, incoming token validation, scope/role policies, backend composition | Application, Infrastructure |
| Contracts | Explicit API request/response contracts; no EF entities | None |
| Client | Client workflows/state, HTTP capability interfaces, platform abstractions | Contracts |
| Client.Api | HTTP implementations, serialization, transport errors, credential forwarding | Client |
| UI | Reusable Razor pages, layouts, components, and assets | Client |
| Web | Interactive Server startup, routing, web identity, browser storage, client composition | UI, Client.Api |

Backend application services enforce authoritative rules. Client validation improves usability but does not grant permissions. Interfaces describe meaningful capabilities. Simple CRUD stays simple; entity methods protect local invariants without moving persistence or identity-provider calls into the domain.

The current connection check demonstrates:

```text
ApiConnectionCheck (UI)
  -> IAppStatusService / AppStatusService (Client)
  -> IAppStatusApi (Client abstraction)
  -> AppStatusApiClient (Client.Api)
  -> GET /api/status (Api)
```

The owner workflow uses `IWishListApi` in Client, `WishListApiClient` in Client.Api, and `IWishListService`/`WishListService` in Application. Infrastructure implements `IWishListStore` and `IUserStore` with EF Core. The shared forms call the transport capability directly; client orchestration services are added when there is actual client workflow logic to separate. Platform-specific delegated token acquisition is behind `IApiAccessTokenProvider`.

## List access

- Anyone may create an account through Entra External ID; an account is required to create/manage lists.
- Lists default to private. Owners can manage their lists without a link.
- A private list requires ownership or a valid bearer share link to view.
- Public lists allow anonymous viewing. Public discovery/search is deferred.
- Nonowners can mark purchases if authenticated and viewing a public list, or if they possess a valid share link for that list.
- An authenticated nonowner without a share link cannot access a private list.
- Authenticated owner identity takes precedence over a share link. The owner sees the owner view and cannot mark purchases in v1.
- Admin is a placeholder operational role. It does not override ownership or share access.
- Archived lists are excluded from ordinary access.

`ListAccessResolver` matches presented credentials to stored hashes before calling `ListAccessService`. Internal user IDs come from validated external identities; guest IDs come from validated guest credentials. Revocation and expiration are checked for each view, guest issuance, mark, and undo operation. Invalid bearer tokens never silently become anonymous requests.

## Sharing

- Any number of links per list, with no product-level cap.
- Optional UTC expiration; at the expiration instant the link is no longer active.
- A link stays unchanged until revoked or expired. Revocation never silently rotates other links.
- Link access is transferable: anyone holding the token has the granted access.
- Cryptographically random tokens use 32 bytes of entropy. Store their SHA-256 hash for validation and a separately protected value for the owner to copy the same link later.
- Never include raw tokens in logs, telemetry, exception text, or ordinary list responses. Owner-only link-management responses can deliver authorized link values.
- Shared-page responses must prevent cross-viewer caching and token disclosure. The web shell already sets `Referrer-Policy: no-referrer`; full share routing and cache controls are implemented with sharing.

## Purchase marks and guest ownership

- `ItemPurchase` is a separate entity/table containing item ID, positive integer quantity, creation time, an idempotency key, and exactly one owner: `User` or `GuestIdentity`.
- A mark means reserved/marked; List Hero does not validate that a purchase happened.
- Buyers may undo their own active marks. A share link alone does not grant ownership of all marks made through it.
- Anonymous buyers receive a separate guest credential. Store its hash on the server; persist the credential through the host's storage adapter. The browser adapter uses protected local storage after the UI becomes interactive.
- Guest issuance/validation and purchase endpoints are implemented. Possessing a guest credential alone does not grant list access: a valid share link is still required for anonymous purchase actions, including undo.
- Browser storage/key loss can prevent recovery of anonymous marks. New devices cannot recover the credential from the share URL.
- Signing in does not automatically transfer guest marks to a user; a valid guest credential can continue to establish ownership while present.
- Overpurchasing is allowed. Active totals of 6 against a desired quantity of 4 show fulfillment and 2 units over the request; no inventory reservation constraint applies.
- Totals use a 64-bit integer so sums of valid individual quantities do not overflow a 32-bit integer.
- Repeated delivery of the same request must return the original mark rather than create another. A unique `(WishListItemId, IdempotencyKey)` index is scaffolded; the request handler must also verify actor/payload equality on a retry.
- Row versions protect edits/undo from lost updates; they do not cap purchase totals.

## Owner privacy and editing

- Owner list responses never include purchase totals, fulfillment, buyer identity, timestamps, or purchase records. Separate owner/viewer DTOs protect this boundary.
- Owner reads do not need to load purchase records. Viewer reads expose totals but not buyer identities.
- Viewing while logged out with a link, or under a different identity, can reveal purchase information. This is accepted behavior.
- Edit preflight may return a minimal warning flag if active purchase marks exist. This intentionally reveals limited information when editing; ordinary owner reads remain purchase-free.
- Suggested warning: "Someone may already have chosen this item as a gift. Updating the price, photo, or quantity is usually fine. If you're replacing it with a different gift, consider removing this item and adding a new one."
- Warn, then let the owner make any edit. Do not reject changes because of purchase marks.
- Purchase marks remain attached to the edited item. Deleting/replacing an item archives its history instead of repurposing it automatically.
- An owner reveal/reset toggle is deferred.

## Data and persistence

- Entity names: `User`, `WishList`, `WishListItem`, `ListShareLink`, `ItemPurchase`, `GuestIdentity`.
- Item-to-list is many-to-one through `WishListId`; list-to-owner is many-to-one through `OwnerId`.
- `double?` approximate unit price in USD initially; reject negative/non-finite values.
- Desired quantity is a positive integer, default 1. Priority and display order are separate integers; ranking UX is still to be specified.
- Name/description fields, optional absolute HTTP(S) product URL, and optional external image URL. Upload/blob storage is deferred.
- Application-generated GUIDs identify entities; time values use `DateTimeOffset` and should be supplied from a backend clock.
- Archive instead of physical deletion. DbContext rejects tracked deletes, and FK delete behavior is restrictive. Archived filtering is explicit to preserve access for future maintenance workflows.
- User identity maps the validated issuer and stable object identity to an internal GUID; email is not an ownership key. App registration subject identifiers can differ across apps, so the API remains the authority for mapping users.
- Roles come from validated Entra app roles. Standard access does not require an explicit role claim. Admin assignment never implies content access.

## Execution and deployment

The web host uses Interactive Server. Shared components do not specify web render modes; the host owns that choice. A future MAUI Blazor Hybrid host will execute the shared UI locally and provide different storage/authentication implementations.

Scoped UI/client state belongs to a Blazor circuit; never register user-specific state as a singleton or assume a circuit is an HTTP request. The web token provider resolves the current authenticated principal explicitly from `AuthenticationStateProvider`. The typed API client requests a token in the caller's scope; no circuit state lives in a pooled HTTP message handler. Do not persist a request-bound HttpContext as circuit state. Initial list reads run after interactivity to avoid duplicate prerender requests. Owner routes and API responses use no-store cache control.

Azure App Service and Azure SQL are the initial direction. Entra registrations are configured. Deployment infrastructure, managed identities, persistent encrypted Data Protection key rings, shared token caches, and operational observability still require configuration before deployment. No Azure hosting or database resources have been created.

## Core workflow status and next work

1. Initial identity/owner milestone: implemented. The real Entra registrations and customer flow are configured; customer sign-in and list/item creation succeeded, with validated API tokens and persisted LocalDB records. Live sign-out, renewal, and second-customer isolation checks remain to be completed.
2. Owner edits, archive actions, visibility changes, row-version concurrency handling, and conditional edit warnings: implemented and tested.
3. Share-link management and private/public viewer routes, credential validation, no-store responses, and token-safe transport: implemented and tested.
4. Guest credentials, idempotent purchase creation/undo, fulfillment UI, and periodic viewer refresh: implemented and tested, including a real browser guest workflow.
5. Next: complete live Entra sign-out/renewal/second-customer checks; refine ordering and usability; prepare Azure deployment with persistent keys/cache and reviewed migrations. Public discovery, uploads, admin operations, owner reveal/reset, and MAUI startup remain future features.

The initial migration has been applied to local development SQL Server; the core workflows require no additional migration. The coverage milestone adds a 150-case suite with five SQL scenarios, a repeatable browser regression, Web host integration, and per-assembly CI coverage requirements. See [test coverage](test-coverage.md). No Azure hosting or database resources have been created.
