# HTTP and Chromium feasibility on the existing Azure beta

Tested November 19, 2026. The owner authorized a bounded test on current hosting, with no paid services or hosting upgrades. The extension is an optional future route; pasted links remain the preferred primary workflow.

## Environment and scope

- Existing API App Service `listhero-api-yj3pk6oimchdi`, Windows F1, .NET 10, 32-bit worker setting. The actual console reported .NET 10.0.15 and a 32-bit supervisor. Browser/driver binaries were the existing Playwright 1.63.0 Chromium headless-shell build 1243.
- Commands ran through authenticated Kudu in a new private `HOME/data/ListHero.MetadataProbe-<nonce>` directory, outside the deployed application. Neither deployed application code nor settings were changed, and no public test endpoint was added. The database, certificates, and key rings were not used by the probe.
- Initial API persistent-storage usage was about 52 MiB, with a reported 1 GiB storage limit. The staged browser was about 270 MiB and the driver/probe package about 103 MiB before compression. Staging was temporary and removed afterward.
- This establishes **host-level feasibility in the Kudu execution context**, not a completed API integration. Kudu and the main site can have different execution-context restrictions. A real API-worker smoke test is still needed before shipping the feature. This was not a concurrency, endurance, or capacity test.

## Actual results

The product inputs were the user's Amazon ASIN `B0FTT5B1C3` and REI product `237596` with `color=MOUNTAIN`. All browsing used fresh unauthenticated contexts; there were no retailer accounts, cookies from the user's browser, paid providers, proxies, or CAPTCHA-solving services.

| Approach | Amazon TV | REI boots |
| --- | --- | --- |
| Ordinary .NET HTTP | HTTP 200, 3,781-character generic Amazon page, no product fields | 15-second timeout |
| Chromium, page JavaScript enabled | Correct product name/image; price absent | HTTP/2 protocol error |
| Chromium, HTTP/2 disabled, REI-only follow-up | Not retried in this variation | 20-second navigation timeout |
| Lean Chromium, page JavaScript and scripts/styles/images/media/fonts blocked | Correct product name/image; price absent | Not retried after the two browser failures |

The successful image matched the previous local research: `https://m.media-amazon.com/images/I/81rH0lDB-rL._AC_SL1500_.jpg`. Ordinary HTML retrieval needs a fallback for this demonstrated case. Chromium can retrieve the useful Amazon HTML without running the retailer's page JavaScript in the lean mode; other retailers may still require scripts. No reliable REI extraction or price/description extraction is claimed.

## Timing and sampled resources

Browser launch took about 1.3–1.5 seconds. Amazon page navigation plus the deliberate 1.5-second extraction wait took about 3.6 seconds in lean mode and 4.6–5.0 seconds with page scripts enabled. These timings exclude parts of supervisor/driver initialization and disposal; they are not end-to-end UI latency guarantees. Measurement runs also deliberately held the page open for 12 seconds to obtain resource samples.

Five Kudu process snapshots were taken per resource run. Totals below sum the matching .NET supervisor, Node driver, and Chromium processes; they exclude the existing web/API/Kudu workers. Processes were sampled sequentially, not at one atomic instant. Summed working sets can double-count shared pages. Private memory is a committed-memory counter, not an exact measure of resident RAM or Azure quota accounting.

| Observed maximum across snapshots | Page scripts enabled | Lean mode |
| --- | ---: | ---: |
| Sum of process working sets | 601.4 MiB | 420.5 MiB |
| Sum of private memory | 545.3 MiB | 269.9 MiB |

The earlier 44–65 MiB supervisor peaks did **not** include Node/Chromium and must not be presented as the full browser cost. The lean mode reduced the observed footprint, but a browser remains material on a small free host. Existing app traffic and arbitrary retailers can change the budget substantially.

## Outcome and next implementation boundary

The current free Windows host **can launch Chromium and extract the demonstrated Amazon product**, without a subscription or tier change. This supports a small HTTP-first, lean-browser-fallback prototype. It does not establish universal retailer support or production readiness.

Before adding it to the main item form:

1. Verify the same approach from the actual API worker, with one import at a time, cancellation, hard time/body limits, and caching/deduplication. Do not enable concurrent browser imports on the strength of this test.
2. Make useful partial results acceptable. A missing price remains missing; preserve user-entered fields and the original URL. Reject blocked pages and irrelevant logos rather than claiming success from HTTP 200.
3. Implement the untrusted URL-fetch boundary, including subrequest/redirect/network restrictions for browser navigation, without forwarding Entra credentials or retailer sessions. The test used only fixed public URLs; it is not a safe general-purpose fetch API.
4. Leave REI unresolved until a real product extraction succeeds. Preserve manual entry and the option of a later extension, without making extension installation necessary for other users or treating a URL-only mobile share as a rendered page.

No paid service, new Azure resource, upgraded plan, app setting, migration, application deployment, or GitHub push was used for this feasibility test.

## Cleanup and evidence

The exact temporary directory was removed after checking its nonce, resolved parent path, lack of reparse points, and that the recorded probe process had exited. The cleanup command reported `Removed=true`. A subsequent Kudu check confirmed the directory was absent and no Node/Chromium probe processes remained. The API plan remained F1/Free with its original runtime, 32-bit worker setting, and `alwaysOn=false`.

Post-test checks passed: web 200; API liveness/status/readiness 200; anonymous owner request 401; missing-list database read 404; and the expected Entra sign-in challenge 302. The web and API also returned 200 during the final resource test. No full application-suite rerun was required: application source and dependencies were not changed.

Reproducible probe sources and raw local evidence are Git-ignored under `.artifacts/link-import-research/AzureProbe/`, including `hosted-result.json`, `hosted-followup-result.json`, `hosted-measure-result.json`, `resource-samples.json`, `hosted-lean-result.json`, `lean-resource-samples.json`, and `cleanup-result.json`. These contain runtime measurements and public product metadata, not app settings or customer records.

References: [Playwright headless-shell installation](https://playwright.dev/dotnet/docs/browsers), [Kudu command/ZIP APIs](https://github.com/projectkudu/kudu/wiki/REST-API), [Windows App Service sandbox](https://github.com/projectkudu/kudu/wiki/Azure-Web-App-sandbox), and [App Service quotas](https://learn.microsoft.com/en-us/azure/app-service/web-sites-monitor).
