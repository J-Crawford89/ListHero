# Product details from pasted links

Item add/edit forms automatically try an HTTPS link when its field changes, and offer **Get item details** to retry. Suggestions fill only currently empty fields. Existing values, including text still being typed, stay in place. Nothing is saved until the user submits the item. Imported values participate in the existing unsaved-change guard and encrypted draft recovery. All fields remain editable.

The authenticated `POST /api/item-metadata` accepts `{ "url": "https://…" }` and returns a `ProductLinkResult`. `found` can be partial; missing values are null. `unavailable` means manual entry is needed. `busy` means the bounded import capacity was exhausted and a later retry may work. There is no retailer-support guarantee. The endpoint requires an active user and the existing API scope; shared-list guest credentials do not grant import access. Responses have `Cache-Control: no-store`.

## Fetching and extraction

1. Try ordinary HTTP for up to six seconds, with at most five validated destinations and four MiB of decompressed HTML.
2. Read schema.org Product JSON-LD, Open Graph/Twitter metadata, and Amazon's product title/main-image elements. Accept an explicit USD price when available. Reduce imported markup to plain text, bound text lengths, and reject known block pages and logo/placeholder images. Do not infer product names from URL slugs or convert currencies.
3. When a useful title or image is missing, try a fresh Chromium headless shell with page JavaScript, service workers, downloads, and non-document requests disabled. This intentionally favors small public HTML responses. Retailers that require scripts, authentication, or a CAPTCHA can still fail.

HTTP and Chromium use no user/Entra credentials, retailer sessions, paid providers, remote browser services, or external proxies. Images remain external HTTPS URLs; they are not uploaded or downloaded by the importer. Variant query strings are preserved, and URL fragments are removed.

## Network and resource boundaries

Only public HTTPS DNS destinations on port 443 are accepted. IP literals, credentials in URLs, local/internal hostnames, and the application's Azure hostnames are rejected. Every actual socket resolves the DNS name and rejects the whole DNS answer if it includes a private, loopback, link-local, multicast, special-use, or Azure platform address. It then connects to a validated IP directly, eliminating a second DNS lookup between validation and connection. Redirects use the same policy.

Chromium uses an ephemeral loopback CONNECT tunnel implemented in the API process. It validates and pins connections while leaving retailer TLS to Chromium. The tunnel accepts only TLS destinations on port 443, bounds connections, headers, time, and upload/download bytes, and closes with the import. Browser navigation is limited to the main document and a small number of redirects. This is not an open proxy or a general URL fetching endpoint.

On the current single-instance F1 host:

- One import runs at a time, without a waiting queue. Excess concurrent requests return `busy`.
- Each user gets up to ten attempts per minute. Chromium has a separate process-wide budget of sixty attempts per hour.
- Overall import work has a thirty-five-second cancellation budget; the client allows forty-five seconds including transport/disposal. HTTP, browser launch/navigation, and tunnel transfers also have shorter bounds.
- A size-bounded in-process cache keeps successful/partial metadata for six hours and failures for two minutes. URL cache keys are SHA-256 digests. User limits are enforced before cache hits. Cache/limits reset on process restart; these are free-host safeguards, not distributed abuse controls.
- Event 4000 logs outcome and transport; event 4001 logs a failed transport and exception type. Neither includes the submitted URL, query, HTML, credentials, or exception message.

CI downloads the headless shell pinned by Microsoft.Playwright during packaging and includes it only in the Windows API artifact. There are no runtime downloads or new Azure resources. Linux CI explicitly requests Windows browser assets for the existing Windows host. `ProductImport:BrowserPath` can override the executable for local development; local Playwright installations are also supported. A hosted application with missing packaged assets fails the lookup safely.

## Verification

Automated coverage includes parsing, connection/address policy, validated redirect handling, response limits, cache/rate/concurrency behavior, active-account/API authorization, HTTP client errors, stale/canceled imports, real-browser network rejection, and an API/form/draft-recovery/SQL journey with deterministic retailer fixtures. CI retailer fixtures do not contact live shops.

The local release check passed all 620 tests with no skips and all ten coverage gates. The final diagnostic change also passed the 69 importer tests.

The integrated implementation retrieved the supplied Amazon TV's correct title, description, and main image from both the full URL and `a.co` short link locally. Price was absent. REI product 237596 still returned no usable details. The earlier host feasibility evidence and limitations are in [link-import-azure-feasibility.md](link-import-azure-feasibility.md). Actual Azure API-worker verification must be recorded separately from that Kudu probe.
