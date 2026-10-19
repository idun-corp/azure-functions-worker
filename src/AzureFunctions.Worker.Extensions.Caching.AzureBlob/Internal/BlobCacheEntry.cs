using System.Globalization;
using Microsoft.Extensions.Caching.Distributed;

namespace AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;

/// <summary>
/// The bookkeeping a cache entry carries in blob metadata, alongside its payload.
/// </summary>
/// <remarks>
/// Blob storage offers no server-side filtering, so expiry is decided here, on read, rather than by
/// the backend. Timestamps are round-trip ISO-8601 in UTC: metadata values have to be ASCII, and
/// that format also sorts lexicographically, which keeps it usable as a blob index tag should tag
/// eviction ever need one.
/// </remarks>
internal readonly record struct BlobCacheEntry
{
    /// <summary>
    /// Schema version of the metadata. An entry written by a future, incompatible version reads as
    /// a miss rather than being misinterpreted.
    /// </summary>
    public const string CurrentSchema = "1";

    private const string SchemaName = "cacheschema";
    private const string KeyName = "cachekey";
    private const string CreatedAtName = "cachecreatedat";
    private const string ExpiresAtName = "cacheexpiresat";
    private const string AbsoluteExpirationName = "cacheabsoluteexpiration";
    private const string SlidingSecondsName = "cacheslidingseconds";
    private const string EncodingName = "cacheencoding";
    private const string LengthName = "cachelength";

    /// <summary>
    /// The cache key this entry was written under, recovered in full even when the blob name had to
    /// be truncated.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// When the entry was written.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the entry stops being served. Moves forward as a sliding entry is refreshed.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// The ceiling a sliding refresh may never move <see cref="ExpiresAt"/> past.
    /// </summary>
    public DateTimeOffset? AbsoluteExpiration { get; init; }

    /// <summary>
    /// Length of the sliding window, when the entry has one.
    /// </summary>
    public long? SlidingExpirationSeconds { get; init; }

    /// <summary>
    /// Compression applied to the payload, or null when it is stored verbatim.
    /// </summary>
    public string? Encoding { get; init; }

    /// <summary>
    /// Length of the payload before compression.
    /// </summary>
    public long Length { get; init; }

    /// <summary>
    /// Builds an entry for <see cref="IDistributedCache"/>, which supports both sliding and absolute
    /// expiration.
    /// </summary>
    /// <remarks>
    /// Deliberately identical to the Azure Table cache's calculation, including the detail that the
    /// configured default sliding expiration applies only when the caller supplied neither kind of
    /// expiration. The two caches are expected to behave the same way, and a test pins them together.
    /// </remarks>
    /// <param name="key">Cache key</param>
    /// <param name="options">Caller supplied expiration</param>
    /// <param name="now">Current time</param>
    /// <param name="defaultSlidingExpiration">Fallback when the caller supplied no expiration</param>
    /// <param name="length">Payload length before compression</param>
    /// <param name="encoding">Compression applied to the payload</param>
    /// <returns>The entry to write</returns>
    public static BlobCacheEntry Create(
        string key,
        DistributedCacheEntryOptions options,
        DateTimeOffset now,
        TimeSpan defaultSlidingExpiration,
        long length,
        string? encoding)
    {
        var absoluteExpiration = options.AbsoluteExpiration ?? (options.AbsoluteExpirationRelativeToNow.HasValue
            ? now.Add(options.AbsoluteExpirationRelativeToNow.Value)
            : (DateTimeOffset?) null);

        var slidingExpiration = options.SlidingExpiration ?? (absoluteExpiration.HasValue
            ? (TimeSpan?) null
            : defaultSlidingExpiration);

        var expiresAt = slidingExpiration.HasValue
            ? now.Add(slidingExpiration.Value)
            : absoluteExpiration!.Value;

        if (expiresAt > absoluteExpiration)
        {
            expiresAt = absoluteExpiration.Value;
        }

        return new BlobCacheEntry
        {
            Key = key,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            AbsoluteExpiration = absoluteExpiration,
            SlidingExpirationSeconds = slidingExpiration.HasValue
                ? (long) slidingExpiration.Value.TotalSeconds
                : null,
            Encoding = encoding,
            Length = length,
        };
    }

    /// <summary>
    /// Builds an entry for the output cache store, whose only expiration is a fixed lifetime.
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="validFor">How long the entry stays fresh</param>
    /// <param name="now">Current time</param>
    /// <param name="length">Payload length before compression</param>
    /// <param name="encoding">Compression applied to the payload</param>
    /// <returns>The entry to write</returns>
    public static BlobCacheEntry Create(
        string key,
        TimeSpan validFor,
        DateTimeOffset now,
        long length,
        string? encoding)
    {
        var expiresAt = now.Add(validFor);

        return new BlobCacheEntry
        {
            Key = key,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            AbsoluteExpiration = expiresAt,
            SlidingExpirationSeconds = null,
            Encoding = encoding,
            Length = length,
        };
    }

    /// <summary>
    /// Reads an entry back out of blob metadata.
    /// </summary>
    /// <param name="metadata">Blob metadata</param>
    /// <param name="entry">The parsed entry</param>
    /// <returns>Whether the metadata described an entry this version understands</returns>
    public static bool TryParse(IDictionary<string, string>? metadata, out BlobCacheEntry entry)
    {
        entry = default;

        if (metadata is null || metadata.Count == 0)
        {
            return false;
        }

        // Metadata names are case-insensitive in the service, and the comparer on the dictionary the
        // SDK hands back is not part of its contract, so normalise rather than assume.
        var values = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

        if (!values.TryGetValue(SchemaName, out var schema) || schema != CurrentSchema)
        {
            return false;
        }

        if (!BlobCacheKeyEncoder.TryDecodeKeyFromMetadata(Read(values, KeyName), out var key) ||
            !TryReadTimestamp(values, CreatedAtName, out var createdAt) ||
            !TryReadTimestamp(values, ExpiresAtName, out var expiresAt))
        {
            return false;
        }

        entry = new BlobCacheEntry
        {
            Key = key,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt,
            AbsoluteExpiration = TryReadTimestamp(values, AbsoluteExpirationName, out var absolute)
                ? absolute
                : null,
            SlidingExpirationSeconds = long.TryParse(
                Read(values, SlidingSecondsName),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var sliding)
                    ? sliding
                    : null,
            Encoding = Read(values, EncodingName),
            Length = long.TryParse(
                Read(values, LengthName),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var length)
                    ? length
                    : 0,
        };

        return true;
    }

    /// <summary>
    /// Renders the entry as blob metadata.
    /// </summary>
    /// <returns>Metadata to attach to the blob</returns>
    public IDictionary<string, string> ToMetadata()
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SchemaName] = CurrentSchema,
            [KeyName] = BlobCacheKeyEncoder.EncodeKeyForMetadata(this.Key),
            [CreatedAtName] = Format(this.CreatedAt),
            [ExpiresAtName] = Format(this.ExpiresAt),
            [LengthName] = this.Length.ToString(CultureInfo.InvariantCulture),
        };

        if (this.AbsoluteExpiration.HasValue)
        {
            metadata[AbsoluteExpirationName] = Format(this.AbsoluteExpiration.Value);
        }

        if (this.SlidingExpirationSeconds.HasValue)
        {
            metadata[SlidingSecondsName] = this.SlidingExpirationSeconds.Value
                .ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(this.Encoding))
        {
            metadata[EncodingName] = this.Encoding;
        }

        return metadata;
    }

    /// <summary>
    /// Whether the entry has lapsed and must not be served.
    /// </summary>
    /// <param name="now">Current time</param>
    /// <returns>Whether the entry has expired</returns>
    public bool IsExpired(DateTimeOffset now)
    {
        return this.ExpiresAt <= now;
    }

    /// <summary>
    /// Whether enough of the sliding window has elapsed to be worth a metadata write.
    /// </summary>
    /// <param name="now">Current time</param>
    /// <param name="threshold">Fraction of the window that must have elapsed</param>
    /// <returns>Whether the entry should be refreshed</returns>
    public bool ShouldRefresh(DateTimeOffset now, double threshold)
    {
        if (!this.SlidingExpirationSeconds.HasValue || this.SlidingExpirationSeconds.Value <= 0)
        {
            return false;
        }

        // Already pinned to its ceiling: a refresh could not move it, so it would be a pure cost.
        if (this.AbsoluteExpiration.HasValue && this.ExpiresAt >= this.AbsoluteExpiration.Value)
        {
            return false;
        }

        var window = TimeSpan.FromSeconds(this.SlidingExpirationSeconds.Value);
        var elapsed = now - (this.ExpiresAt - window);

        return elapsed >= (window * threshold);
    }

    /// <summary>
    /// The same entry with its sliding window restarted, clamped to the absolute expiration.
    /// </summary>
    /// <param name="now">Current time</param>
    /// <returns>The refreshed entry</returns>
    public BlobCacheEntry Refresh(DateTimeOffset now)
    {
        if (!this.SlidingExpirationSeconds.HasValue)
        {
            return this;
        }

        var expiresAt = now.AddSeconds(this.SlidingExpirationSeconds.Value);

        if (expiresAt > this.AbsoluteExpiration)
        {
            expiresAt = this.AbsoluteExpiration!.Value;
        }

        return this with { ExpiresAt = expiresAt };
    }

    private static string Format(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? Read(Dictionary<string, string> values, string name)
    {
        return values.TryGetValue(name, out var value) ? value : null;
    }

    private static bool TryReadTimestamp(
        Dictionary<string, string> values,
        string name,
        out DateTimeOffset value)
    {
        value = default;

        return values.TryGetValue(name, out var raw) &&
            DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out value);
    }
}
