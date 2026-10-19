namespace Microsoft.Extensions.Caching.Distributed;

/// <summary>
/// JSON and bulk operations the <see cref="IDistributedCache"/> contract does not cover, available
/// when the cache is backed by Azure Blob Storage.
/// </summary>
public static class DistributedCacheExtensions
{
    /// <summary>
    /// Gets a value and deserializes it.
    /// </summary>
    /// <typeparam name="T">Value type</typeparam>
    /// <param name="cache">Cache</param>
    /// <param name="key">Cache key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The value, or the type's default when absent</returns>
    public static Task<T?> GetJsonAsync<T>(
        this IDistributedCache cache,
        string key,
        CancellationToken cancellationToken = default)
    {
        return AsBlobCache(cache).GetJsonAsync<T>(key, cancellationToken);
    }

    /// <summary>
    /// Serializes a value and stores it.
    /// </summary>
    /// <typeparam name="T">Value type</typeparam>
    /// <param name="cache">Cache</param>
    /// <param name="key">Cache key</param>
    /// <param name="value">Value to store</param>
    /// <param name="options">Expiration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that completes when the value is stored</returns>
    public static Task SetJsonAsync<T>(
        this IDistributedCache cache,
        string key,
        T value,
        DistributedCacheEntryOptions options,
        CancellationToken cancellationToken = default)
    {
        return AsBlobCache(cache).SetJsonAsync(key, value, options, cancellationToken);
    }

    /// <summary>
    /// Stores several values under one expiration.
    /// </summary>
    /// <typeparam name="T">Value type</typeparam>
    /// <param name="cache">Cache</param>
    /// <param name="entries">Values by cache key</param>
    /// <param name="options">Expiration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that completes when every value is stored</returns>
    public static Task SetAllJsonAsync<T>(
        this IDistributedCache cache,
        IReadOnlyDictionary<string, T> entries,
        DistributedCacheEntryOptions options,
        CancellationToken cancellationToken = default)
    {
        return AsBlobCache(cache).SetAllJsonAsync(entries, options, cancellationToken);
    }

    /// <summary>
    /// Gets every live value whose key matches the pattern.
    /// </summary>
    /// <remarks>
    /// A pattern ending in "*" matches by prefix; anything else is an exact key.
    /// </remarks>
    /// <typeparam name="T">Value type</typeparam>
    /// <param name="cache">Cache</param>
    /// <param name="keyPattern">Key, or key prefix followed by "*"</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching values</returns>
    public static Task<List<T>> SearchJsonAsync<T>(
        this IDistributedCache cache,
        string keyPattern,
        CancellationToken cancellationToken = default)
    {
        return AsBlobCache(cache).SearchJsonAsync<T>(keyPattern, cancellationToken);
    }

    /// <summary>
    /// Gets every live value whose key matches the pattern, keyed by the key it was stored under.
    /// </summary>
    /// <typeparam name="T">Value type</typeparam>
    /// <param name="cache">Cache</param>
    /// <param name="keyPattern">Key, or key prefix followed by "*"</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching values by cache key</returns>
    public static Task<Dictionary<string, T>> SearchJsonWithKeysAsync<T>(
        this IDistributedCache cache,
        string keyPattern,
        CancellationToken cancellationToken = default)
    {
        return AsBlobCache(cache).SearchJsonWithKeysAsync<T>(keyPattern, cancellationToken);
    }

    /// <summary>
    /// Removes the named entries.
    /// </summary>
    /// <param name="cache">Cache</param>
    /// <param name="keys">Cache keys</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that completes when the entries are removed</returns>
    public static Task RemoveAllAsync(
        this IDistributedCache cache,
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        return AsBlobCache(cache).RemoveAllAsync(keys, cancellationToken);
    }

    /// <summary>
    /// Removes every entry whose key matches the pattern.
    /// </summary>
    /// <param name="cache">Cache</param>
    /// <param name="keyPattern">Key, or key prefix followed by "*"</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task that completes when the entries are removed</returns>
    public static Task RemoveAllAsync(
        this IDistributedCache cache,
        string keyPattern,
        CancellationToken cancellationToken = default)
    {
        return AsBlobCache(cache).RemoveAllAsync(keyPattern, cancellationToken);
    }

    private static AzureBlobCache AsBlobCache(IDistributedCache cache)
    {
        if (cache is not AzureBlobCache blobCache)
        {
            throw new NotSupportedException(
                $"These operations are specific to the Azure Blob cache, but {nameof(IDistributedCache)} " +
                $"is implemented by {cache.GetType().Name}. Register it with AddAzureBlobCache.");
        }

        return blobCache;
    }
}
