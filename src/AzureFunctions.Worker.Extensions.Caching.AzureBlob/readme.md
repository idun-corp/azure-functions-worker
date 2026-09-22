# AzureFunctions.Worker.Extensions.Caching.AzureBlob

Azure Blob Storage as a cache backend, in two shapes over one container:

- **`IDistributedCache`** — absolute and sliding expiration, wildcard search and bulk removal. The
  sibling of `AzureFunctions.Worker.Extensions.Caching.AzureTable`, for payloads past the 1 MiB an
  Azure Table entity can hold.
- **`IOutputCacheStore`** — ASP.NET Core output caching, so a plain ASP.NET Core API gets a shared,
  process-independent output cache with no custom middleware.

## Distributed cache

```csharp
builder.Services.AddAzureBlobCache(cacheOptions =>
{
    cacheOptions.ContainerName = "cache";
    cacheOptions.ApplicationName = "MyApp";
    cacheOptions.DefaultSlidingExpiration = TimeSpan.FromMinutes(20);
});
```

`ConnectionString` falls back to the `AzureWebJobsStorage` configuration value and then to the
environment variable of the same name, so a Functions app normally configures nothing.

## Output cache

```csharp
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("Hourly", policy => policy.Expire(TimeSpan.FromHours(1)));
});

builder.Services.AddAzureBlobOutputCache(cacheOptions =>
{
    cacheOptions.ContainerName = "response-cache";
    cacheOptions.ApplicationName = "MyApp";
});

app.UseOutputCache();
```

Everything ASP.NET Core output caching provides — named policies, vary-by rules, automatic
`If-None-Match` revalidation and resource locking against cache stampedes — applies unchanged.

### Eviction by tag is prefix purge only

`EvictByTagAsync` acts on exactly one tag, `PurgeAllTag` (default `response-cache:all`), and removes
every entry belonging to the application. **Any other tag is logged and ignored** rather than
throwing, because a failed purge is a better outcome than a failed request.

Per-tag eviction needs an index, and blob index tags are a poor fit: they are eventually consistent,
need `tags/read` and `filter/action` permissions separate from data access, are billed per tag per
month, exist only on GPv2 and premium block blob accounts, and Azurite's filtering is still
unreliable. The intended design is one **append blob** per tag accumulating member keys, since
`AppendBlockAsync` is atomically append-only, which is the atomicity tagging needs. Until then, keep
using one tag.

## How entries are stored

One blob per entry, named `{ApplicationName}/{escaped key}`. Expiry, the original key and the
payload encoding live in blob **metadata**, which a download returns alongside the payload, so a read
costs a single request.

**Expiry is enforced on read.** Blob storage has no server-side filter, so every read checks the
entry's own expiry and treats a lapsed entry as a miss, deleting it in the background. An entry is
never served stale, whether or not the sweep has run. `ExpiredItemsDeletionInterval` only controls
when storage is reclaimed, and setting it to `TimeSpan.Zero` hands that job to a lifecycle policy:

```json
{
  "rules": [{
    "name": "expire-cache",
    "type": "Lifecycle",
    "definition": {
      "filters": { "blobTypes": [ "blockBlob" ], "prefixMatch": [ "cache/MyApp/" ] },
      "actions": { "baseBlob": { "delete": { "daysAfterModificationGreaterThan": 1 } } }
    }
  }]
}
```

A lifecycle policy is a safety net, not a mechanism: its conditions are measured in whole days and a
change takes up to 24 hours to take effect, which is why read-time checking is not optional.

**Sliding expiration is refreshed with a metadata-only write**, and never under an ETag
precondition — a refresh that loses a race would otherwise surface as a failed 412 dependency for
something that is not a failure. `SlidingExpirationRefreshThreshold` (default 0.5) skips the write
until the window is half spent, which removes most of the writes a hot key would otherwise pay for.

**Payloads are compressed with Brotli above 4 KiB** by default. The encoding is recorded in private
metadata rather than in `Content-Encoding`, because that header invites transparent decompression by
`HttpClient`, AzCopy, Storage Explorer or a CDN, which would make the stored form depend on how it
was read. An entry whose encoding this version does not recognise reads as a miss, so changing the
default is always safe.

## Cache misses are 404s, and that is visible in telemetry

A miss is a `BlobNotFound`, and the Azure SDK marks its client-level activity failed before this
library swallows the exception. Application Insights therefore records a failed `Microsoft.Storage`
dependency per miss.

Probing with `ExistsAsync` first would avoid it, at the cost of **two** requests on every hit to tidy
the miss path — the wrong trade. Prefer a telemetry processor that rewrites a failed blob dependency
carrying `ErrorCode: BlobNotFound` to a success, keeping the record so a surge of misses stays
visible. `EnableDependencyTracing = false` is the blunt alternative: it silences the misses and every
genuine storage failure with them.

## Keys

A key is percent-escaped per character, so prefix search stays exact and the whole key lives in one
path segment. Keys whose escaped form exceeds `MaxEncodedKeyLength` (700) are truncated and suffixed
with a hash of the full key; the full key is kept in metadata, so a truncated name can still be
matched exactly and a hash collision is detected rather than served.
