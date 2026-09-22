using System.Buffers;
using System.IO.Pipelines;
using AzureFunctions.Worker.Extensions.Caching.AzureBlob.Internal;
using Microsoft.Extensions.Caching.Distributed;

namespace Microsoft.AspNetCore.OutputCaching;

/// <summary>
/// Azure Blob Storage as an <see cref="IOutputCacheStore"/>, so output caching survives a process
/// restart and is shared across instances.
/// </summary>
internal sealed class AzureBlobOutputCacheStore : IOutputCacheBufferStore, IDisposable
{
    /// <summary>
    /// Blob name segment separating these entries from the distributed cache's, so that a prefix
    /// purge on one face cannot reach the other.
    /// </summary>
    internal const string Scope = "output";

    private readonly AzureBlobCacheOptions options;
    private readonly BlobCacheStorage storage;
    private readonly ILogger<AzureBlobOutputCacheStore> logger;

    public AzureBlobOutputCacheStore(
        IOptions<AzureBlobCacheOptions> cacheOptions,
        TimeProvider timeProvider,
        ILogger<AzureBlobOutputCacheStore> logger)
    {
        this.options = cacheOptions.Value;
        this.logger = logger;
        this.storage = new BlobCacheStorage(cacheOptions, Scope, timeProvider, logger);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBlobOutputCacheStore"/> class.
    /// </summary>
    /// <remarks>
    /// Takes a storage layer built by the caller, so the store can be exercised without an account.
    /// </remarks>
    /// <param name="cacheOptions">Cache options</param>
    /// <param name="logger">Logger</param>
    /// <param name="storage">Storage layer</param>
    internal AzureBlobOutputCacheStore(
        IOptions<AzureBlobCacheOptions> cacheOptions,
        ILogger<AzureBlobOutputCacheStore> logger,
        BlobCacheStorage storage)
    {
        this.options = cacheOptions.Value;
        this.logger = logger;
        this.storage = storage;
    }

    /// <inheritdoc />
    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var result = await this.storage.ReadAsync(key, cancellationToken);

        return result?.Payload;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryGetAsync(
        string key,
        PipeWriter destination,
        CancellationToken cancellationToken)
    {
        var result = await this.storage.ReadAsync(key, cancellationToken);

        if (result is null)
        {
            return false;
        }

        await destination.WriteAsync(result.Payload, cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public ValueTask SetAsync(
        string key,
        byte[] value,
        string[]? tags,
        TimeSpan validFor,
        CancellationToken cancellationToken)
    {
        // Tags are accepted and not stored. Eviction is prefix based in this version, so per-tag
        // bookkeeping would be written and never read; see EvictByTagAsync.
        _ = tags;

        return new ValueTask(this.WriteAsync(key, value, validFor, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask SetAsync(
        string key,
        ReadOnlySequence<byte> value,
        ReadOnlyMemory<string> tags,
        TimeSpan validFor,
        CancellationToken cancellationToken)
    {
        _ = tags;

        return new ValueTask(this.WriteAsync(key, value.ToArray(), validFor, cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        if (!string.Equals(tag, this.options.PurgeAllTag, StringComparison.Ordinal))
        {
            // Deliberately not an exception. Eviction runs from maintenance endpoints and background
            // jobs, where failing the caller is worse than not evicting; a log makes the gap visible
            // without turning a missing feature into an outage.
            this.logger.LogWarning(
                "Output cache eviction by tag {Tag} was ignored. This store evicts only {PurgeAllTag}, which clears every entry",
                tag,
                this.options.PurgeAllTag);

            return;
        }

        var deleted = await this.storage.DeleteByNamePrefixAsync(
            this.storage.ScopePrefix(),
            cancellationToken);

        this.logger.LogInformation("Output cache purged, removing {DeletedCount} entries", deleted);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this.storage.Dispose();
    }

    private Task WriteAsync(string key, byte[] value, TimeSpan validFor, CancellationToken cancellationToken)
    {
        var now = this.storage.UtcNow;

        return this.storage.WriteAsync(
            key,
            value,
            (length, encoding) => BlobCacheEntry.Create(key, validFor, now, length, encoding),
            cancellationToken);
    }
}
