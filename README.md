# BookAggregator

ASP.NET Core (.NET 10) API aggregation assessment. Searches Open Library, Apple Books and dBooks concurrently and maps their responses to a common book model. No database, API keys or additional production packages are required beyond the existing ASP.NET Core OpenAPI package.

## Setup and run

Prerequisites: a stable .NET 10 SDK, access to NuGet for the first restore, and outbound HTTPS access to the three providers for live searches. `global.json` accepts stable .NET 10 feature bands starting at 10.0.100. Tests use local HTTP stubs and do not call external APIs.

From the repository root:

```sh
dotnet restore BookAggregator.slnx
dotnet build BookAggregator.slnx --no-restore --configuration Release
dotnet test BookAggregator.slnx --no-build --configuration Release
dotnet run --project BookAggregator.Api --launch-profile http
```

The HTTP profile listens at `http://localhost:5203` in Development. Stop with Ctrl+C. Open `BookAggregator.slnx` in Rider to run/debug the same profiles and tests. Example requests are also available in `BookAggregator.Api/BookAggregator.Api.http`.

```sh
curl -i 'http://localhost:5203/api/books?query=frankenstein'
curl -i 'http://localhost:5203/api/books?query=python&source=DBooks&sortBy=title'
curl -i 'http://localhost:5203/api/books?query=frankenstein&author=shelley&minYear=1800&maxYear=1900&sortBy=publishedYear&sortOrder=desc'
curl 'http://localhost:5203/api/statistics'
curl 'http://localhost:5203/openapi/v1.json'
```

Generated OpenAPI JSON is available at `/openapi/v1.json` in Development. There is no Swagger UI dependency. The `https` launch profile additionally listens at `https://localhost:7159`; using it requires a trusted ASP.NET development certificate. The HTTP-only profile works without a certificate; HTTPS redirection may log that no HTTPS port is configured. For deployment, configure HTTPS directly or through the hosting reverse proxy.

## Search endpoint

`GET /api/books`

| Query parameter | Default | Validation and behavior |
| --- | --- | --- |
| `query` | Required | Nonblank, maximum 200 characters; outer whitespace trimmed before searching/caching. Case is preserved. |
| `source` | All three | `OpenLibrary`, `AppleBooks`, `DBooks`, case insensitive; outer whitespace trimmed. Only the selected provider is called. |
| `author` | None | Nonblank when supplied, maximum 200 characters; case insensitive substring match against any author. |
| `category` | None | Nonblank when supplied, maximum 200 characters; case insensitive substring match against any category. |
| `minYear` | None | Integer from 1 to 9999, inclusive lower bound. |
| `maxYear` | None | Integer from 1 to 9999, inclusive upper bound; must be at least `minYear`. |
| `sortBy` | `relevance` | `relevance`, `title`, `publishedYear`, `source`, case insensitive. |
| `sortOrder` | `asc` | `asc` or `desc`, case insensitive. Ignored for `relevance`. |

All supplied filters are combined with AND. Unknown publication years are excluded by year filters and placed last when sorting by year in either direction. Missing categories/authors do not match those filters. Text comparisons use ordinal case insensitive comparison. Sorting uses title, source and source ID to resolve ties deterministically. `relevance` preserves upstream order within each provider and provider registration order; it is not a combined relevance score. Results retain their provenance; books from different providers are not deduplicated.

Filters and sorting operate on the retrieved search results, not on the full upstream catalog. Open Library and Apple Books requests ask for 10 results; an upstream service may return a different number. dBooks returns up to 100. No extra detail requests or upstream pagination are performed.

Successful responses preserve the original JSON array contract. Example (illustrative metadata):

```json
[
  {
    "source": "OpenLibrary",
    "sourceId": "/works/OL450063W",
    "title": "Frankenstein",
    "authors": ["Mary Shelley"],
    "publishedYear": 1818,
    "categories": ["Fiction"],
    "url": "https://openlibrary.org/works/OL450063W"
  }
]
```

`source`, `sourceId`, `title` are strings. `authors` and `categories` are arrays, including empty arrays for unknown metadata. `publishedYear` is an integer or null; `url` is a string or null. Open Library supplies a work's first publication year; Apple supplies the release year of its store entry. These values need not agree. dBooks search does not supply year/categories, so those fields remain null/empty instead of triggering one HTTP request per book. dBooks HTML entities are decoded and its comma-separated author field is split into individual authors.

### Status codes and degraded results

| Situation | HTTP response |
| --- | --- |
| At least one requested provider is usable, including valid empty results | `200`, JSON array (possibly `[]`) |
| A requested provider fails while another succeeds | `200`, available results; `X-Unavailable-Providers` lists failed providers |
| A failed provider has usable stale cache data | `200`, includes stale books; provider appears in both `X-Stale-Providers` and `X-Unavailable-Providers` |
| All requested providers fail, but at least one has usable stale data | `200` with the same diagnostic headers |
| All requested providers fail with no usable cache | `503`, `application/problem+json` |
| Invalid or missing input | `400`, `application/problem+json` (`ValidationProblemDetails`), no external calls |
| Unexpected application error | `500`, handled by ASP.NET exception handling/ProblemDetails |

Headers contain comma-separated provider names. Absence of a header means no provider has that condition. Read the headers when the completeness/freshness of results matters. Responses use `Cache-Control: no-store`; the cache described below is internal server-side storage, not client/proxy caching.

Example all-provider failure:

```json
{
  "title": "No book provider is currently available.",
  "status": 503,
  "detail": "Retry later. No usable cached results are available for this query."
}
```

ProblemDetails may also contain `type`, `instance`, `traceId` or other framework fields. Caller cancellation is propagated through cache waits, HTTP attempts and retry delays; it is not converted to an empty success response. A disconnected client is not guaranteed to receive a response body/status.

## HTTP resilience

Each provider uses a typed `HttpClient` from `IHttpClientFactory`. `ProviderHttpHandler` applies a small GET-only policy without adding a production dependency:

- A 3-second timeout per attempt includes both headers and the entire response body.
- At most one retry (two attempts total) for network `HttpRequestException`, attempt timeout, HTTP 408, 429 or 5xx. Other HTTP failures and JSON/mapping failures are not retried.
- Exponential backoff starts at 200 ms, adds 0–49 ms jitter, and is capped at 1 second. A valid `Retry-After` delta/date is respected; if it exceeds the allowed delay, return the failure immediately instead of retrying early or waiting indefinitely.
- With defaults, a provider uses at most approximately 7 seconds for two network attempts and a bounded retry delay, excluding runtime scheduling and local parsing. Providers execute concurrently.
- Response bodies are limited to 2 MiB per attempt. Caller cancellation stops both active requests and backoff, without retry.

Malformed/missing required response envelopes, HTTP errors, network failures and provider timeouts are isolated to that provider. Invalid book rows (missing source ID/title) are skipped. Missing optional metadata is allowed. Programming errors are allowed to reach the central exception handler rather than being silently presented as provider outages.

## Cache behavior and concurrency

`ProviderSearchCache` is a singleton with a dedicated, size-limited `MemoryCache`. Entries are keyed by provider name and trimmed query. Case is preserved; different query case may have different upstream semantics. Filtering/sorting are applied after retrieval and do not create extra cache entries.

- Successful results, including successful empty results, are fresh for 120 seconds.
- After freshness expires, the next caller attempts a refresh. When refresh fails, previous successful data can be returned for up to 900 seconds **from the original successful fetch**. Failures never extend this deadline.
- After a failed fetch/refresh, the same provider/query is not retried for 10 seconds. During backoff, return still-valid stale data or an explicit unavailable result; a failure is never cached as a successful empty search.
- A keyed asynchronous lock coalesces concurrent identical requests. Different providers and different queries have independent locks. Locks are removed when their last holder/waiter finishes; query keys do not accumulate indefinitely.
- A cancelled waiter does not cancel the owner. If the owner cancels, it does not cache a failure; the next waiting caller can perform its own fetch using its own cancellation token. There is no detached background fetch.
- A maximum of 1000 entries is retained (each entry has size 1); expiry, eviction/admission and process restart may cause earlier cache misses. The limit is on entries, not bytes. Cached book collections are exposed as read-only collections.

Cache/statistics are per-process, intentionally nonpersistent and not shared between replicas. There is no background refresh, distributed cache or circuit breaker; the per-query failure backoff reduces repeated failures while allowing other queries to proceed.

## Statistics endpoint

`GET /api/statistics` returns every registered provider, including zero counts before traffic. Example:

```json
[
  {
    "source": "OpenLibrary",
    "totalRequests": 3,
    "successfulRequests": 2,
    "failedRequests": 1,
    "cancelledRequests": 0,
    "averageResponseTimeMs": 750,
    "fastRequests": 1,
    "averageRequests": 2,
    "slowRequests": 0,
    "fastMeanResponseTimeMs": 250,
    "averageMeanResponseTimeMs": 1000,
    "slowMeanResponseTimeMs": 0
  }
]
```

Statistics count **completed outbound HTTP attempts**, including retries, HTTP failures, network errors, timeouts and caller cancellations. They do not count cache hits, retry waits, requests still in flight, or incoming client requests. HTTP 2xx is a successful HTTP attempt even if the provider later rejects the JSON payload. `failedRequests` excludes caller cancellations, which are counted separately. Measurement includes response body transfer and excludes JSON mapping.

- Fast: duration `< 500 ms`.
- Average: duration `>= 500 ms` and `< 2000 ms`.
- Slow: duration `>= 2000 ms`.
- Each bucket reports the mean duration of its own completed attempts in milliseconds, rounded to two decimals. An empty bucket reports 0.
- `averageResponseTimeMs` averages all completed attempts for that provider, weighted by the number of attempts in each bucket. It reports 0 before traffic.

For every snapshot, `totalRequests = successfulRequests + failedRequests + cancelledRequests = fastRequests + averageRequests + slowRequests`. A concurrent dictionary and a per-provider lock protect both updates and snapshots. Each provider row is internally consistent; the entire array is not a globally simultaneous snapshot. Counts reset on process restart.

## Configuration

Defaults live in validated option classes; existing appsettings files do not need to be changed. Override via `appsettings.json` sections `ProviderHttp` / `BookCache`, or environment variables:

| Environment variable | Default | Allowed range |
| --- | --- | --- |
| `ProviderHttp__AttemptTimeoutSeconds` | 3 | 1–30 |
| `ProviderHttp__MaxRetries` | 1 | 0–2 |
| `ProviderHttp__RetryDelayMilliseconds` | 200 | 1–1000 |
| `ProviderHttp__MaxRetryDelayMilliseconds` | 1000 | 1–5000; at least the base retry delay |
| `BookCache__FreshSeconds` | 120 | 1–3600 |
| `BookCache__StaleSeconds` | 900 | 1–86400; at least the fresh duration |
| `BookCache__FailureBackoffSeconds` | 10 | 1–300 |
| `BookCache__MaxEntries` | 1000 | 1–10000 |

Invalid configuration fails at startup. Provider base addresses and registration are centralized in `Configuration/ServiceCollectionExtensions.cs`. To add another source, implement `IBookProvider` and register it there with `AddProvider<T>`; aggregation, cache, source validation and statistics discover registered names automatically. Names must be unique.

## Code structure

```text
BookAggregator.Api/
  Configuration/    Validated HTTP/cache options and provider DI registration
  Controllers/      Input validation, HTTP status/header handling
  Infrastructure/   GET retry/timeout/statistics handler and keyed async lock
  Models/           Unified books, request validation, results and statistics
  Providers/        IBookProvider plus three upstream JSON adapters
  Services/         Concurrent aggregation/filtering/sorting, cache, statistics
BookAggregator.Tests/
  ProviderTests.cs       JSON mapping, invalid/empty responses and URL encoding
  AggregationTests.cs    Concurrency, partial failures, filters, sorts, cancellation
  CacheTests.cs          Fresh/stale lifetime, coalescing, backoff and cancellation
  HttpPolicyTests.cs     Retry limits, HTTP statuses, timeouts, body limits, cancellation
  StatisticsTests.cs     Buckets, averages and concurrent updates/snapshots
  ConfigurationTests.cs  Invalid policy settings
  ApiTests.cs            Real ASP.NET pipeline with stubbed outbound HTTP
```

## Verification and mandatory requirement audit

All automated tests are deterministic with respect to provider responses; no live API availability is required. Cache time is controlled by a test clock. Timeout tests exercise short real cancellation timers. Integration tests retain the actual DI registrations, typed clients, resilience handlers, middleware, model binding and serialization, replacing only the final outbound transport (and the clock for cache-lifetime scenarios).

| Mandatory requirement | Implementation | Verification |
| --- | --- | --- |
| 1. ASP.NET aggregation and extensibility | `Program`, `IBookProvider`, `ServiceCollectionExtensions`, `BookAggregationService` | `ApiTests.RealPipelineAggregatesThreeProvidersAndCacheHitsDoNotIncreaseHttpStatistics`; source selection test |
| 2. Three external APIs | `OpenLibraryProvider`, `AppleBooksProvider`, `DBooksProvider` | `ProviderTests` mapping/encoding/error/empty-result cases; three-source pipeline test; live smoke checks |
| 3. Concurrent asynchronous calls | `BookAggregationService.SearchAsync`, `Task.WhenAll` | `AggregationTests.AllProvidersStartBeforeAnyCompletes`; cancellation test |
| 4. Unified aggregation endpoint | `BooksController`, `BookDto` | `ApiTests` response/status/serialization tests; live `/api/books` checks |
| 5. Validated filtering and sorting | `BookSearchRequest`, `BooksController`, `BookAggregationService` | `AggregationTests` combined filters and both sort directions; `ApiTests` validation and combined-filter tests |
| 6. Error/transient failure handling and fallback | `ProviderHttpHandler`, `ProviderSearchCache`, `BooksController`, exception middleware | `HttpPolicyTests`; partial/all failure, invalid JSON, stale fallback/expiry integration tests |
| 7. Meaningful unit tests | `BookAggregator.Tests` in the solution | `dotnet test BookAggregator.slnx` (includes integration tests) |
| 8. API and setup documentation | This README, `.http` examples, generated OpenAPI | `ApiTests.OpenApiDocumentDescribesBothEndpointsAndParameters`; clean-source build/run verification |
| 9. Cache reduces redundant requests | `ProviderSearchCache`, `AsyncKeyedLock` | `CacheTests`; concurrent API/coalescing test; cache-hit statistics integration/live checks |
| 10. Thread-safe per-provider statistics | `ProviderStatistics`, `ProviderHttpHandler`, `StatisticsController` | `StatisticsTests.ConcurrentUpdatesAndSnapshotsHaveNoLostUpdatesOrInconsistentTotals`; bucket boundary tests; HTTP retry and API tests |

JWT authentication and anomaly detection are optional and intentionally not implemented. No new production dependencies were introduced. Test-only packages are xUnit, the .NET test SDK/runner, and ASP.NET Core MVC Testing.

### Verified delivery checks (2026-11-21)

- Release build with `-warnaserror`: 0 warnings, 0 errors.
- 94 automated tests passed, 0 failed, 0 skipped.
- Clean-source verification: copied the complete current source (including uncommitted additions) without `.git`, `bin` or `obj`; restored packages into a new empty package directory with `--no-http-cache`; built Release, ran all tests and started the API successfully. This verifies the current local source rather than the older remote checkout. SDK used: 10.0.400.
- On the clean run, `/api/statistics` and `/openapi/v1.json` returned 200; invalid year bounds returned 400. The HTTP launch profile was used with an explicit temporary port override.
- Live `/api/books?query=frankenstein` returned 200 with 10 Open Library, 12 Apple Books and 4 dBooks results, with no unavailable/stale provider headers. These are observed results, not a fixed catalog assertion.
- Live dBooks-only `python` search returned 100 results. Repeated searches, filtered searches and 10 parallel cached requests caused no additional outbound HTTP attempts. Invalid input caused no outbound attempts.
- External failure/timeout conditions were verified using controlled transport stubs, not by intentionally disrupting live services.
- `dotnet format --verify-no-changes --no-restore --severity warn` and `git diff --check` passed.

Provider contracts: [Open Library Search API](https://openlibrary.org/dev/docs/api/search), [Apple Search API](https://performance-partners.apple.com/search-api), [dBooks API](https://www.dbooks.org/api/). Availability and catalog contents can change independently of the application.
