using AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;

namespace Microsoft.Extensions.Caching.Distributed;

/// <summary>
/// Azure Blob Storage as an <see cref="IDistributedCache"/>.
/// </summary>
internal sealed class AzureBlobCache : IDistributedCache, IDisposable
{
    /// <summary>
    /// Blob name segment separating these entries from the output cache store's, so that a prefix
    /// purge on one face cannot reach the other.
    /// </summary>
    internal const string Scope = "distributed";

    private readonly AzureBlobCacheOptions options;
    private readonly IOptions<JsonSerializerOptions> serializerOptions;
    private readonly BlobCacheStorage storage;

    public AzureBlobCache(
        IOptions<AzureBlobCacheOptions> cacheOptions,
        IOptions<JsonSerializerOptions> serializerOptions,
        TimeProvider timeProvider,
        ILogger<AzureBlobCache> logger)
    {
        this.options = cacheOptions.Value;
        this.serializerOptions = serializerOptions;
        this.storage = new BlobCacheStorage(cacheOptions, Scope, timeProvider, logger);
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var result = await this.storage.ReadAsync(key, token);

        if (result is null)
        {
            return null;
        }

        this.RefreshIfDue(result.Name, result.Entry);

        return result.Payload;
    }

    /// <inheritdoc />
    public byte[]? Get(string key)
    {
        var result = this.storage.Read(key);

        if (result is null)
        {
            return null;
        }

        this.RefreshIfDue(result.Name, result.Entry);

        return result.Payload;
    }

    /// <inheritdoc />
    public Task SetAsync(
        string key,
        byte[] value,
        DistributedCacheEntryOptions options,
        CancellationToken token = default)
    {
        return this.storage.WriteAsync(key, value, this.Describe(key, options), token);
    }

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        this.storage.Write(key, value, this.Describe(key, options));
    }

    /// <inheritdoc />
    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        // Deliberately a properties call rather than a download: refreshing an entry has no use for
        // its payload, and these payloads are the reason this cache exists.
        var result = await this.storage.ReadMetadataAsync(key, token);

        if (result is { } found)
        {
            this.RefreshIfDue(found.Name, found.Entry);
        }
    }

    /// <inheritdoc />
    public void Refresh(string key)
    {
        this.RefreshAsync(key).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default)
    {
        return this.storage.RemoveAsync(key, token);
    }

    /// <inheritdoc />
    public void Remove(string key)
    {
        this.storage.Remove(key);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.storage.Dispose();
    }

    internal async Task<T?> GetJsonAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var payload = await this.GetAsync(key, cancellationToken);

        return payload is null
            ? default
            : JsonSerializer.Deserialize<T>(payload, this.serializerOptions.Value);
    }

    internal Task SetJsonAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions entryOptions,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, this.serializerOptions.Value);

        return this.SetAsync(key, payload, entryOptions, cancellationToken);
    }

    internal async Task SetAllJsonAsync<T>(
        IReadOnlyDictionary<string, T> entries,
        DistributedCacheEntryOptions entryOptions,
        CancellationToken cancellationToken = default)
    {
        // Blob storage has no batch upload, so these go out concurrently rather than as one request.
        // The degree of parallelism is bounded because each payload is held in memory while it flies.
        await Parallel.ForEachAsync(
            entries,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Environment.ProcessorCount * 2,
            },
            async (entry, token) => await this.SetJsonAsync(entry.Key, entry.Value, entryOptions, token));
    }

    internal async Task<List<T>> SearchJsonAsync<T>(
        string keyPattern,
        CancellationToken cancellationToken = default)
    {
        var values = new List<T>();

        await foreach (var (key, payload) in this.SearchAsync(keyPattern, cancellationToken))
        {
            _ = key;

            var value = JsonSerializer.Deserialize<T>(payload, this.serializerOptions.Value);

            if (value is not null)
            {
                values.Add(value);
            }
        }

        return values;
    }

    internal async Task<Dictionary<string, T>> SearchJsonWithKeysAsync<T>(
        string keyPattern,
        CancellationToken cancellationToken = default)
    {
        var values = new Dictionary<string, T>(StringComparer.Ordinal);

        await foreach (var (key, payload) in this.SearchAsync(keyPattern, cancellationToken))
        {
            var value = JsonSerializer.Deserialize<T>(payload, this.serializerOptions.Value);

            if (value is not null)
            {
                values[key] = value;
            }
        }

        return values;
    }

    internal async Task RemoveAllAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        var names = keys
            .Select(key => BlobCacheKeyEncoder.Encode(
                key,
                string.Concat(this.options.ApplicationName, "/", Scope),
                this.options.MaxEncodedKeyLength))
            .ToList();

        await this.storage.DeleteAsync(names, cancellationToken);
    }

    internal async Task RemoveAllAsync(string keyPattern, CancellationToken cancellationToken = default)
    {
        var names = new List<string>();

        await foreach (var (name, _) in this.storage.ListAsync(LiteralPrefix(keyPattern), cancellationToken))
        {
            names.Add(name);
        }

        await this.storage.DeleteAsync(names, cancellationToken);
    }

    private static string LiteralPrefix(string keyPattern)
    {
        return keyPattern.EndsWith('*') ? keyPattern[..^1] : keyPattern;
    }

    private async IAsyncEnumerable<(string Key, byte[] Payload)> SearchAsync(
        string keyPattern,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Without a trailing wildcard this is a single-key lookup, matching the Azure Table cache.
        if (!keyPattern.EndsWith('*'))
        {
            var single = await this.GetAsync(keyPattern, cancellationToken);

            if (single is not null)
            {
                yield return (keyPattern, single);
            }

            yield break;
        }

        var prefix = LiteralPrefix(keyPattern);

        await foreach (var (_, entry) in this.storage.ListAsync(prefix, cancellationToken))
        {
            // The listing carries metadata but not payloads, so each match costs its own read. That
            // read also re-checks expiry and refreshes the sliding window, which a listing cannot do.
            var payload = await this.GetAsync(entry.Key, cancellationToken);

            if (payload is not null)
            {
                yield return (entry.Key, payload);
            }
        }
    }

    private Func<long, string?, BlobCacheEntry> Describe(string key, DistributedCacheEntryOptions entryOptions)
    {
        var now = this.storage.UtcNow;

        return (length, encoding) => BlobCacheEntry.Create(
            key,
            entryOptions,
            now,
            this.options.DefaultSlidingExpiration,
            length,
            encoding);
    }

    private void RefreshIfDue(string name, BlobCacheEntry entry)
    {
        if (entry.ShouldRefresh(this.storage.UtcNow, this.options.SlidingExpirationRefreshThreshold))
        {
            this.storage.ScheduleRefresh(name, entry);
        }
    }
}
