namespace Microsoft.Extensions.Caching.Distributed;

/// <summary>
/// Options for the Azure Blob Storage cache.
/// </summary>
public sealed class AzureBlobCacheOptions
{
    /// <summary>
    /// Storage connection string. When left unset it falls back to the "AzureWebJobsStorage"
    /// configuration value, and then to the environment variable of the same name.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Container holding the cache entries. Created on first use unless
    /// <see cref="CreateContainerIfNotExists"/> is disabled.
    /// </summary>
    public string ContainerName { get; set; } = "cache";

    /// <summary>
    /// Blob name prefix isolating one application's entries within the container.
    /// </summary>
    public string ApplicationName { get; set; } = "Default";

    /// <summary>
    /// How often expired entries are swept. <see cref="TimeSpan.Zero"/> disables the sweep, leaving
    /// cleanup to a storage lifecycle policy. Reads stay correct either way: an expired entry is
    /// never served, whether or not it has been swept.
    /// </summary>
    public TimeSpan ExpiredItemsDeletionInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Sliding expiration applied when the caller supplies neither a sliding nor an absolute
    /// expiration.
    /// </summary>
    public TimeSpan DefaultSlidingExpiration { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Fraction of the sliding window that must have elapsed before a read refreshes the entry's
    /// expiry. Zero refreshes on every read.
    /// </summary>
    /// <remarks>
    /// Every refresh is a billable write. The Azure Table cache refreshes on every read, which a hot
    /// key makes expensive; deferring until the window is half spent removes most of those writes
    /// and cannot shorten an entry's life, because the refresh still extends from the time it runs.
    /// </remarks>
    public double SlidingExpirationRefreshThreshold { get; set; } = 0.5;

    /// <summary>
    /// Compression applied to payloads at or above <see cref="CompressionThresholdBytes"/>.
    /// </summary>
    public BlobCacheCompression Compression { get; set; } = BlobCacheCompression.Brotli;

    /// <summary>
    /// Payload size at which compression starts being applied.
    /// </summary>
    public int CompressionThresholdBytes { get; set; } = 4 * 1024;

    /// <summary>
    /// Longest encoded cache key kept verbatim in a blob name. Longer keys are truncated and
    /// suffixed with a hash of the full key.
    /// </summary>
    public int MaxEncodedKeyLength { get; set; } = 700;

    /// <summary>
    /// Creates the container on first use. Disable when the identity has no create permission and
    /// the container is provisioned out of band.
    /// </summary>
    public bool CreateContainerIfNotExists { get; set; } = true;

    /// <summary>
    /// Deletes in batches of 256 rather than one request per blob. Disable for storage that does not
    /// implement the blob batch endpoint.
    /// </summary>
    public bool UseBatchDelete { get; set; } = true;

    /// <summary>
    /// The only tag the output cache store's eviction acts on. Evicting it removes every entry
    /// belonging to this application; any other tag is logged and ignored.
    /// </summary>
    public string PurgeAllTag { get; set; } = "response-cache:all";

    /// <summary>
    /// Emits Azure SDK dependency telemetry for cache operations.
    /// </summary>
    /// <remarks>
    /// A cache miss is a 404, and the Azure SDK marks its client-level activity failed before this
    /// library swallows the exception, so misses arrive as failed dependencies. Turning this off
    /// silences them, but silences genuine storage failures with them. Prefer rewriting the miss to
    /// a success in a telemetry processor and leaving this on.
    /// </remarks>
    public bool EnableDependencyTracing { get; set; } = true;
}
